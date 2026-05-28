using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mithril.Shared.Telemetry.Abstractions;
using Mithril.Shared.Telemetry.Catalog;
using Mithril.Shared.Telemetry.Export;
using Mithril.Shared.Telemetry.Settings;
using Mithril.Shell.ViewModels;
using Xunit;

namespace Mithril.Shell.Tests;

public sealed class TelemetrySettingsViewModelTests
{
    private sealed class StaticProvider : ITagDescriptorProvider
    {
        private readonly TagDescriptor[] _descriptors;
        public StaticProvider(params TagDescriptor[] descriptors) => _descriptors = descriptors;
        public IReadOnlyCollection<TagDescriptor> Describe() => _descriptors;
    }

    private static TagCatalog BuildCatalog(params TagDescriptor[] descriptors)
        => new TagCatalog(new[] { new StaticProvider(descriptors) });

    private static TagDescriptor Safe(string key, string subsystem = "Mithril.Test")
        => new(key, PiiClassification.Safe, subsystem, $"desc-{key}");

    private static TagDescriptor Sensitive(string key, string subsystem = "Mithril.Test")
        => new(key, PiiClassification.Sensitive, subsystem, $"desc-{key}");

    private static TelemetrySettingsViewModel BuildVm(
        TelemetrySettings? settings = null,
        TagCatalog? catalog = null,
        NewlySeenTagsObserver? observer = null,
        ExporterHealthMonitor? health = null)
    {
        return new TelemetrySettingsViewModel(
            settings ?? new TelemetrySettings(),
            catalog ?? BuildCatalog(Safe("module.id")),
            new HeaderValueProtection(),
            observer ?? new NewlySeenTagsObserver(),
            health ?? new ExporterHealthMonitor());
    }

    [Fact]
    public void Ctor_PopulatesTagGroups_FromCatalog_WithCatalogDefaults()
    {
        var catalog = BuildCatalog(
            Safe("module.id", "Mithril.A"),
            Sensitive("chat.body", "Mithril.B"));

        using var vm = BuildVm(catalog: catalog);

        vm.TagGroups.Should().HaveCount(2);
        var a = vm.TagGroups.Single(g => g.Subsystem == "Mithril.A");
        var b = vm.TagGroups.Single(g => g.Subsystem == "Mithril.B");

        a.Chips.Single(c => c.Key == "module.id").IsExported.Should().BeTrue();
        b.Chips.Single(c => c.Key == "chat.body").IsExported.Should().BeFalse();
    }

    [Fact]
    public void TogglingChip_MutatesTagExports_AndRaisesPropertyChanged()
    {
        var settings = new TelemetrySettings();
        var catalog = BuildCatalog(Safe("module.id"));

        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        using var vm = BuildVm(settings, catalog);
        var chip = vm.TagGroups.Single().Chips.Single();

        chip.IsExported = false;

        settings.TagExports.Should().ContainKey("module.id").WhoseValue.Should().BeFalse();
        raised.Should().Contain(nameof(TelemetrySettings.TagExports));
    }

    [Fact]
    public void Ctor_RespectsExistingUserOverride_OverCatalogDefault()
    {
        // Safe descriptor defaults to true; user overrode to false.
        var settings = new TelemetrySettings();
        settings.TagExports["module.id"] = false;
        var catalog = BuildCatalog(Safe("module.id"));

        using var vm = BuildVm(settings, catalog);

        vm.TagGroups.Single().Chips.Single().IsExported.Should().BeFalse();
    }

    [Fact]
    public void SaveHeader_WrapsValueViaDpapi_BeforeStoring()
    {
        var settings = new TelemetrySettings();
        using var vm = BuildVm(settings);

        vm.AddHeaderCommand.Execute(null);
        var entry = vm.Headers.Single();
        entry.Name = "X-API-Key";
        entry.Value = "supersecret";
        vm.SaveHeaderCommand.Execute(entry);

        settings.Headers.Should().ContainKey("X-API-Key");
        settings.Headers["X-API-Key"].Should().StartWith("dpapi:");
        settings.Headers["X-API-Key"].Should().NotContain("supersecret");
    }

    [Fact]
    public void RemoveHeader_RemovesFromSettingsHeaders()
    {
        var settings = new TelemetrySettings();
        settings.Headers["X-API-Key"] = "dpapi:wrapped-blob";
        using var vm = BuildVm(settings);

        var entry = vm.Headers.Single();
        vm.RemoveHeaderCommand.Execute(entry);

        vm.Headers.Should().BeEmpty();
        settings.Headers.Should().NotContainKey("X-API-Key");
    }

    [Fact]
    public void NewlySeenObserver_OnNewKey_AddsChip()
    {
        var observer = new NewlySeenTagsObserver();
        using var vm = BuildVm(observer: observer);

        observer.Note("surprise.tag");

        vm.NewlySeenChips.Should().ContainSingle(c => c.Key == "surprise.tag");
    }

    [Fact]
    public void PromoteNewlySeen_AddsTagExport_AndRemovesChip()
    {
        var settings = new TelemetrySettings();
        var observer = new NewlySeenTagsObserver();
        observer.Note("surprise.tag");
        using var vm = BuildVm(settings, observer: observer);

        var chip = vm.NewlySeenChips.Single();
        vm.PromoteNewlySeenCommand.Execute(chip);

        settings.TagExports.Should().ContainKey("surprise.tag").WhoseValue.Should().BeTrue();
        vm.NewlySeenChips.Should().BeEmpty();
    }

    [Fact]
    public void ExporterHealth_Pulse_UpdatesLastExportStatus()
    {
        using var health = new ExporterHealthMonitor();
        using var vm = BuildVm(health: health);

        vm.LastExportStatus.Should().Be("No activity yet");

        health.RecordSuccess();

        vm.LastExportStatus.Should().StartWith("Last successful export");
    }

    [Fact]
    public void Dispose_UnsubscribesFromHealthMonitor()
    {
        using var health = new ExporterHealthMonitor();
        var vm = BuildVm(health: health);

        vm.Dispose();
        var statusAfterDispose = vm.LastExportStatus;

        health.RecordSuccess();

        vm.LastExportStatus.Should().Be(statusAfterDispose);
    }

    [Fact]
    public void Dispose_UnsubscribesFromNewlySeenObserver()
    {
        var observer = new NewlySeenTagsObserver();
        var vm = BuildVm(observer: observer);

        vm.Dispose();
        observer.Note("after.dispose");

        vm.NewlySeenChips.Should().BeEmpty();
    }
}
