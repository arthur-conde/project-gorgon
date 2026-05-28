using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Mithril.Shared.Telemetry.Abstractions;
using Mithril.Shared.Telemetry.Catalog;
using Mithril.Shared.Telemetry.Processing;
using Mithril.Shared.Telemetry.Settings;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Processing;

public class AllowlistAndRedactionProcessorTests
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

    private static AllowlistAndRedactionProcessor Build(
        TagDescriptor[] catalog,
        ConcurrentDictionary<string, bool>? userOverrides = null,
        Func<string?>? activeChar = null,
        NewlySeenTagsObserver? observer = null)
    {
        var settings = new TelemetrySettings { TagExports = userOverrides ?? new() };
        var monitor = new TestMonitor(settings);
        return new AllowlistAndRedactionProcessor(
            new TagCatalog(new[] { new Provider(catalog) }),
            monitor,
            new ValueRedactor(activeChar ?? (() => null), @"C:\Users\u", @"C:\Users\u\AppData\Local"),
            observer ?? new NewlySeenTagsObserver());
    }

    [Fact]
    public void Drops_tag_whose_key_is_unknown_and_notes_it_as_newly_seen()
    {
        var observer = new NewlySeenTagsObserver();
        var p = Build(catalog: Array.Empty<TagDescriptor>(), observer: observer);

        using var src = new ActivitySource("Mithril.Test");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = p.OnEnd,
        };
        ActivitySource.AddActivityListener(listener);
        using var act = src.StartActivity("op")!;
        act.SetTag("unknown.tag", "value");
        act.Stop();

        act.GetTagItem("unknown.tag").Should().BeNull();
        observer.Snapshot().Should().Contain("unknown.tag");
    }

    [Fact]
    public void Keeps_tag_whose_key_is_in_catalog_and_default_exported()
    {
        var d = new TagDescriptor("module.id", PiiClassification.Identifying, "Mithril.Shell", "");
        var p = Build(catalog: new[] { d });

        using var src = new ActivitySource("Mithril.Test");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = p.OnEnd,
        };
        ActivitySource.AddActivityListener(listener);
        using var act = src.StartActivity("op")!;
        act.SetTag("module.id", "samwise");
        act.Stop();

        act.GetTagItem("module.id").Should().Be("samwise");
    }

    [Fact]
    public void Drops_tag_whose_key_user_disabled_even_if_catalogued()
    {
        var d = new TagDescriptor("module.id", PiiClassification.Identifying, "Mithril.Shell", "");
        var p = Build(catalog: new[] { d }, userOverrides: new() { ["module.id"] = false });

        using var src = new ActivitySource("Mithril.Test");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = p.OnEnd,
        };
        ActivitySource.AddActivityListener(listener);
        using var act = src.StartActivity("op")!;
        act.SetTag("module.id", "samwise");
        act.Stop();

        act.GetTagItem("module.id").Should().BeNull();
    }

    [Fact]
    public void Drops_sensitive_tag_by_default_without_user_override()
    {
        var d = new TagDescriptor("character.name", PiiClassification.Sensitive, "Mithril.Shell", "");
        var p = Build(catalog: new[] { d });

        using var src = new ActivitySource("Mithril.Test");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = p.OnEnd,
        };
        ActivitySource.AddActivityListener(listener);
        using var act = src.StartActivity("op")!;
        act.SetTag("character.name", "Thorgrim");
        act.Stop();

        act.GetTagItem("character.name").Should().BeNull();
    }

    [Fact]
    public void Redacts_character_name_substring_from_passing_string_value()
    {
        var d = new TagDescriptor("error.message", PiiClassification.Identifying, "Mithril.Shell", "");
        var p = Build(catalog: new[] { d }, activeChar: () => "Thorgrim");

        using var src = new ActivitySource("Mithril.Test");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = p.OnEnd,
        };
        ActivitySource.AddActivityListener(listener);
        using var act = src.StartActivity("op")!;
        act.SetTag("error.message", "Thorgrim died");
        act.Stop();

        act.GetTagItem("error.message").Should().Be("$CHARACTER died");
    }
}
