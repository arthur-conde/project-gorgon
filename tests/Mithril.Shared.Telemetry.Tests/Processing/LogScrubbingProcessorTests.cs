using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mithril.Shared.Telemetry.Abstractions;
using Mithril.Shared.Telemetry.Catalog;
using Mithril.Shared.Telemetry.Processing;
using Mithril.Shared.Telemetry.Settings;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Processing;

/// <summary>
/// Behavioural contract for <see cref="LogScrubbingProcessor"/> — the logs-
/// pipeline analogue of <see cref="AllowlistAndRedactionProcessor"/>.
///
/// Tests pipe an <see cref="ILogger"/> through an <see cref="OpenTelemetryLoggerProvider"/>
/// configured with the processor under test plus a capture processor so each
/// emitted <see cref="LogRecord"/> is observed after scrubbing.
/// </summary>
public class LogScrubbingProcessorTests
{
    private sealed class Provider(params TagDescriptor[] ds) : ITagDescriptorProvider
    {
        public IReadOnlyCollection<TagDescriptor> Describe() => ds;
    }

    private sealed class TestMonitor(TelemetrySettings s) : IOptionsMonitor<TelemetrySettings>
    {
        public TelemetrySettings CurrentValue => s;
        public TelemetrySettings Get(string? name) => s;
        public IDisposable OnChange(Action<TelemetrySettings, string?> listener) => new Sub();
        private sealed class Sub : IDisposable { public void Dispose() { } }
    }

    /// <summary>Captures every LogRecord that traverses the pipeline AFTER the SUT.</summary>
    private sealed class CapturingProcessor : BaseProcessor<LogRecord>
    {
        public List<CapturedRecord> Records { get; } = new();

        public override void OnEnd(LogRecord data)
        {
            // LogRecord instances are pooled; snapshot the fields we assert on.
            var attrs = data.Attributes is null
                ? null
                : data.Attributes.ToList();
            Records.Add(new CapturedRecord(data.FormattedMessage, data.Body, attrs));
        }
    }

    private sealed record CapturedRecord(
        string? FormattedMessage,
        string? Body,
        List<KeyValuePair<string, object?>>? Attributes);

    private static (ILoggerFactory factory, CapturingProcessor capture) Build(
        TagDescriptor[] catalog,
        ConcurrentDictionary<string, bool>? userOverrides = null,
        Func<string?>? activeChar = null,
        NewlySeenTagsObserver? observer = null,
        string userProfile = @"C:\Users\u",
        string localAppData = @"C:\Users\u\AppData\Local")
    {
        var settings = new TelemetrySettings { TagExports = userOverrides ?? new() };
        var monitor = new TestMonitor(settings);
        var redactor = new ValueRedactor(activeChar ?? (() => null), userProfile, localAppData);
        var sut = new LogScrubbingProcessor(
            new TagCatalog(new[] { new Provider(catalog) }),
            monitor,
            redactor,
            observer ?? new NewlySeenTagsObserver());
        var capture = new CapturingProcessor();

        var factory = LoggerFactory.Create(b =>
        {
            b.AddOpenTelemetry(o =>
            {
                o.IncludeFormattedMessage = true;
                o.ParseStateValues = true;
                o.AddProcessor(sut);
                o.AddProcessor(capture);
            });
        });
        return (factory, capture);
    }

    [Fact]
    public void Drops_attribute_whose_key_is_unknown_and_notes_it_as_newly_seen()
    {
        var observer = new NewlySeenTagsObserver();
        var (factory, capture) = Build(Array.Empty<TagDescriptor>(), observer: observer);
        using (factory)
        {
            factory.CreateLogger("test").LogInformation("hello {UnknownTag}", "x");
        }

        var attrs = capture.Records.Should().ContainSingle().Which.Attributes;
        attrs.Should().NotContain(kv => kv.Key == "UnknownTag",
            "unknown attribute keys must be dropped fail-closed and surfaced via NewlySeenTagsObserver");
        observer.Snapshot().Should().Contain("UnknownTag");
    }

    [Fact]
    public void Keeps_attribute_whose_key_is_in_catalog_and_default_exported()
    {
        var d = new TagDescriptor("ModuleId", PiiClassification.Identifying, "Mithril.Shell", "");
        var (factory, capture) = Build(new[] { d });
        using (factory)
        {
            factory.CreateLogger("test").LogInformation("active module {ModuleId}", "samwise");
        }

        var attrs = capture.Records.Should().ContainSingle().Which.Attributes;
        attrs.Should().Contain(kv => kv.Key == "ModuleId" && (string?)kv.Value == "samwise");
    }

    [Fact]
    public void Drops_attribute_whose_key_user_disabled_even_if_catalogued()
    {
        var d = new TagDescriptor("ModuleId", PiiClassification.Identifying, "Mithril.Shell", "");
        var (factory, capture) = Build(new[] { d }, userOverrides: new() { ["ModuleId"] = false });
        using (factory)
        {
            factory.CreateLogger("test").LogInformation("active module {ModuleId}", "samwise");
        }

        capture.Records.Should().ContainSingle().Which.Attributes
            .Should().NotContain(kv => kv.Key == "ModuleId");
    }

    [Fact]
    public void Drops_sensitive_attribute_by_default_without_user_override()
    {
        var d = new TagDescriptor("CharacterName", PiiClassification.Sensitive, "Mithril.Shell", "");
        var (factory, capture) = Build(new[] { d });
        using (factory)
        {
            factory.CreateLogger("test").LogInformation("login as {CharacterName}", "Thorgrim");
        }

        capture.Records.Should().ContainSingle().Which.Attributes
            .Should().NotContain(kv => kv.Key == "CharacterName",
                "Sensitive-classified attributes default to NOT exported on the log side, " +
                "matching the span side's three-layer model");
    }

    [Fact]
    public void Redacts_character_name_substring_from_passing_string_attribute_value()
    {
        var d = new TagDescriptor("Message", PiiClassification.Identifying, "Mithril.Shell", "");
        var (factory, capture) = Build(new[] { d }, activeChar: () => "Thorgrim");
        using (factory)
        {
            factory.CreateLogger("test").LogInformation("ev {Message}", "Thorgrim died");
        }

        capture.Records.Should().ContainSingle().Which.Attributes
            .Should().Contain(kv => kv.Key == "Message" && (string?)kv.Value == "$CHARACTER died");
    }

    [Fact]
    public void Redacts_userprofile_path_prefix_from_formatted_message_body()
    {
        var (factory, capture) = Build(Array.Empty<TagDescriptor>(),
            userProfile: @"C:\Users\u",
            localAppData: @"C:\Users\u\AppData\Local");
        using (factory)
        {
            factory.CreateLogger("test").LogInformation(@"path={Path}", @"C:\Users\u\Documents\save.log");
        }

        // Body text typically renders verbatim at the backend — a %USERPROFILE%
        // substring is precisely the class of leak the redactor exists to
        // prevent. We can't assert on Path attribute because "Path" is
        // unknown to the catalog and gets dropped, but FormattedMessage
        // contains the interpolated path and must be scrubbed.
        var rec = capture.Records.Should().ContainSingle().Which;
        rec.FormattedMessage.Should().Contain(@"$USER\Documents\save.log")
            .And.NotContain(@"C:\Users\u");
    }

    [Fact]
    public void Redacts_localappdata_path_prefix_from_formatted_message_body()
    {
        var (factory, capture) = Build(Array.Empty<TagDescriptor>(),
            userProfile: @"C:\Users\u",
            localAppData: @"C:\Users\u\AppData\Local");
        using (factory)
        {
            factory.CreateLogger("test").LogInformation(@"path={Path}", @"C:\Users\u\AppData\Local\Mithril\boot.log");
        }

        var rec = capture.Records.Should().ContainSingle().Which;
        rec.FormattedMessage.Should().Contain(@"$LOCALAPPDATA\Mithril\boot.log")
            .And.NotContain(@"C:\Users\u");
    }

    [Fact]
    public void Redacts_active_character_name_from_formatted_message_body()
    {
        var (factory, capture) = Build(Array.Empty<TagDescriptor>(), activeChar: () => "Thorgrim");
        using (factory)
        {
            factory.CreateLogger("test").LogInformation("login as {Who}", "Thorgrim");
        }

        var rec = capture.Records.Should().ContainSingle().Which;
        rec.FormattedMessage.Should().NotContain("Thorgrim").And.Contain("$CHARACTER");
    }

    [Fact]
    public void Leaves_non_string_attribute_values_alone()
    {
        var d = new TagDescriptor("Count", PiiClassification.Safe, "Mithril.Shell", "");
        var (factory, capture) = Build(new[] { d });
        using (factory)
        {
            factory.CreateLogger("test").LogInformation("count={Count}", 42);
        }

        capture.Records.Should().ContainSingle().Which.Attributes
            .Should().Contain(kv => kv.Key == "Count" && (int)kv.Value! == 42);
    }

    [Fact]
    public void Preserves_OriginalFormat_attribute_so_template_remains_intact()
    {
        // {OriginalFormat} is the MEL message-template carrier; the catalog
        // never declares it but it's load-bearing for backend rendering.
        var d = new TagDescriptor("Count", PiiClassification.Safe, "Mithril.Shell", "");
        var (factory, capture) = Build(new[] { d });
        using (factory)
        {
            factory.CreateLogger("test").LogInformation("count={Count}", 7);
        }

        capture.Records.Should().ContainSingle().Which.Attributes
            .Should().Contain(kv => kv.Key == "{OriginalFormat}" && (string?)kv.Value == "count={Count}",
                "the {OriginalFormat} carrier is structural; dropping it would " +
                "strip the message template the backend uses to render the row");
    }
}
