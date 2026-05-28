using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mithril.Shared.Telemetry.Processing;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Processing;

/// <summary>
/// Behavioural contract for the TrustEndpoint = true log processor
/// (mithril#840 + mithril#841): unknown attribute keys survive (no catalog
/// gate, no allowlist), Sensitive-classified attributes survive — but
/// <see cref="ValueRedactor"/> still scrubs path prefixes and the active
/// character name from string attribute values and from the formatted
/// message body / <see cref="LogRecord.Body"/>.
/// </summary>
public class LogValueRedactionOnlyProcessorTests
{
    private sealed class CapturingProcessor : BaseProcessor<LogRecord>
    {
        public List<CapturedRecord> Records { get; } = new();

        public override void OnEnd(LogRecord data)
        {
            var attrs = data.Attributes is null ? null : data.Attributes.ToList();
            Records.Add(new CapturedRecord(data.FormattedMessage, data.Body, attrs));
        }
    }

    private sealed record CapturedRecord(
        string? FormattedMessage,
        string? Body,
        List<KeyValuePair<string, object?>>? Attributes);

    private static (ILoggerFactory factory, CapturingProcessor capture) Build(
        Func<string?>? activeChar = null,
        string userProfile = @"C:\Users\u",
        string localAppData = @"C:\Users\u\AppData\Local")
    {
        var redactor = new ValueRedactor(activeChar ?? (() => null), userProfile, localAppData);
        var sut = new LogValueRedactionOnlyProcessor(redactor);
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
    public void Keeps_attribute_whose_key_is_unknown_to_the_catalog()
    {
        var (factory, capture) = Build();
        using (factory)
        {
            factory.CreateLogger("test").LogInformation("v={Brand}", "value");
        }

        capture.Records.Should().ContainSingle().Which.Attributes
            .Should().Contain(kv => kv.Key == "Brand" && (string?)kv.Value == "value",
                "TrustEndpoint = true skips the allowlist entirely so log attributes flow " +
                "without catalog membership");
    }

    [Fact]
    public void Keeps_sensitive_classified_attribute_that_the_default_processor_would_drop()
    {
        var (factory, capture) = Build();
        using (factory)
        {
            factory.CreateLogger("test").LogInformation("login as {CharacterName}", "Thorgrim");
        }

        capture.Records.Should().ContainSingle().Which.Attributes
            .Should().Contain(kv => kv.Key == "CharacterName" && (string?)kv.Value == "Thorgrim",
                "the redactor-only processor has no classification awareness — Sensitive " +
                "attributes flow through unredacted at the attribute layer (value redaction " +
                "still applies if the value contains a known PII substring)");
    }

    [Fact]
    public void Redacts_userprofile_prefix_from_string_attribute_value()
    {
        var (factory, capture) = Build();
        using (factory)
        {
            factory.CreateLogger("test").LogInformation(@"p={Path}", @"C:\Users\u\Documents\save.log");
        }

        capture.Records.Should().ContainSingle().Which.Attributes
            .Should().Contain(kv => kv.Key == "Path" && (string?)kv.Value == @"$USER\Documents\save.log");
    }

    [Fact]
    public void Redacts_active_character_name_from_formatted_message_body()
    {
        var (factory, capture) = Build(activeChar: () => "Thorgrim");
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
        var (factory, capture) = Build();
        using (factory)
        {
            factory.CreateLogger("test").LogInformation("n={Count}", 42);
        }

        capture.Records.Should().ContainSingle().Which.Attributes
            .Should().Contain(kv => kv.Key == "Count" && (int)kv.Value! == 42);
    }
}
