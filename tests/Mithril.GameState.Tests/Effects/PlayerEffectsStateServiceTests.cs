using Arda.Abstractions.Logs;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.GameState.Effects;
using Mithril.GameState.Tests.TestSupport;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Effects;

/// <summary>
/// Acceptance-suite for <see cref="PlayerEffectsStateService"/> per issue
/// #590. Each test maps to a lettered acceptance bullet (a–g) in the issue
/// body. The service now consumes Arda domain events via
/// <see cref="TestDomainEventBus"/> rather than raw log lines.
/// </summary>
public sealed class PlayerEffectsStateServiceTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 5, 20, 20, 1, 17, TimeSpan.Zero);

    private static LogLineMetadata Meta(DateTimeOffset? ts = null) =>
        new(ts ?? Stamp, ts ?? Stamp, IsReplay: false);

    private static PlayerEffectsStateService NewService(
        TestDomainEventBus bus, IDiagnosticsSink? diag = null) =>
        new(bus, diag);

    // ---------- (a) live Add (arg4 == True) → Added ----------

    [Fact]
    public async Task LiveAdd_FiresAddedEvent_AndPopulatesActiveSet()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        var seen = new List<EffectEvent>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            await svc.StartAsync(CancellationToken.None);
            bus.Publish(new EffectsAdded([13303], 25042203, Meta()));

            seen.Should().ContainSingle();
            seen[0].Kind.Should().Be(EffectEventKind.Added);
            seen[0].State.CatalogId.Should().Be(13303);
            seen[0].State.SourceCharId.Should().Be(25042203);
            seen[0].State.InstanceId.Should().BeNull();
            seen[0].State.DisplayName.Should().BeNull();

            svc.TryGet(13303, out var state).Should().BeTrue();
            state.CatalogId.Should().Be(13303);
            svc.ActiveEffects.Should().ContainKey(13303);
        }
        finally { await StopAsync(svc); }
    }

    // ---------- (b) re-emit Add (arg4 == False) is additive-only ----------

    [Fact]
    public async Task SnapshotAdd_FillsMissing_AndDoesNotDropPreexistingEntries()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        try
        {
            await svc.StartAsync(CancellationToken.None);

            bus.Publish(new EffectsAdded([13303], 25042203, Meta()));
            bus.Publish(new EffectsAdded([26015], 0, Meta(Stamp.AddSeconds(1))));

            svc.ActiveEffects.Should().ContainKey(13303,
                "the False snapshot is additive-only and must not drop preexisting entries");
            svc.ActiveEffects.Should().ContainKey(26015);

            svc.TryGet(26015, out var snapshotState).Should().BeTrue();
            snapshotState.SourceCharId.Should().Be(0);
        }
        finally { await StopAsync(svc); }
    }

    // ---------- (c) re-apply of already-present catalog id is idempotent ----------

    [Fact]
    public async Task ReApplySameCatalogId_RefreshesTimestamp_AndFiresNoDuplicateAdded()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        var addedCount = 0;
        using var sub = svc.Subscribe(e => { if (e.Kind == EffectEventKind.Added) addedCount++; });

        try
        {
            await svc.StartAsync(CancellationToken.None);

            bus.Publish(new EffectsAdded([13303], 25042203, Meta()));
            bus.Publish(new EffectsAdded([13303], 25042203, Meta(Stamp.AddMinutes(5))));

            addedCount.Should().Be(1, "the second Add for the same catalog id is a re-emit, not a new application");
            svc.TryGet(13303, out var state).Should().BeTrue();
            state.AppliedAt.Should().Be(Stamp.AddMinutes(5),
                "re-apply refreshes AppliedAt per the spec's idempotent timestamp-refresh rule");
        }
        finally { await StopAsync(svc); }
    }

    // ---------- (d) UpdateEffectName correlates to most-recent un-named ----------

    [Fact]
    public async Task UpdateEffectName_CorrelatesToMostRecentUnnamedEntry_AndFiresDisplayNameChanged()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        var seen = new List<EffectEvent>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            await svc.StartAsync(CancellationToken.None);

            bus.Publish(new EffectsAdded([302], 25098977, Meta()));
            bus.Publish(new EffectNameUpdated(259320, "Performance Appreciation, Level 0", Meta()));

            seen.Should().HaveCount(2);
            seen[0].Kind.Should().Be(EffectEventKind.Added);
            seen[0].State.CatalogId.Should().Be(302);
            seen[1].Kind.Should().Be(EffectEventKind.DisplayNameChanged);
            seen[1].State.CatalogId.Should().Be(302);
            seen[1].State.InstanceId.Should().Be(259320);
            seen[1].State.DisplayName.Should().Be("Performance Appreciation, Level 0");

            svc.TryGet(302, out var state).Should().BeTrue();
            state.InstanceId.Should().Be(259320);
            state.DisplayName.Should().Be("Performance Appreciation, Level 0");
        }
        finally { await StopAsync(svc); }
    }

    // ---------- (e) RemoveEffects by instance id fires Removed ----------

    [Fact]
    public async Task RemoveEffects_ByInstanceId_FiresRemoved_AndDropsFromActive()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        var seen = new List<EffectEvent>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            await svc.StartAsync(CancellationToken.None);

            bus.Publish(new EffectsAdded([302], 25098977, Meta()));
            bus.Publish(new EffectNameUpdated(259320, "Performance Appreciation, Level 0", Meta()));
            bus.Publish(new EffectsRemoved([259320], Meta(Stamp.AddSeconds(14))));

            seen.Should().HaveCount(3);
            seen[2].Kind.Should().Be(EffectEventKind.Removed);
            seen[2].State.CatalogId.Should().Be(302);
            seen[2].State.InstanceId.Should().Be(259320);

            svc.TryGet(302, out _).Should().BeFalse();
            svc.ActiveEffects.Should().NotContainKey(302);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task RemoveEffects_OnUnnamedEntry_LeavesEntryActive()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        try
        {
            await svc.StartAsync(CancellationToken.None);

            bus.Publish(new EffectsAdded([13303], 25042203, Meta()));
            bus.Publish(new EffectsRemoved([259278], Meta(Stamp.AddSeconds(1))));

            svc.ActiveEffects.Should().ContainKey(13303,
                "an un-named entry has no InstanceId bridge and cannot be removed by id");
        }
        finally { await StopAsync(svc); }
    }

    // ---------- (f) late Subscribe(default) replays full event log from session start ----------

    [Fact]
    public async Task LateSubscribe_FromSessionStart_ReplaysFullEventLog_InOrder()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        try
        {
            await svc.StartAsync(CancellationToken.None);

            bus.Publish(new EffectsAdded([302], 25098977, Meta()));
            bus.Publish(new EffectNameUpdated(259320, "Performance Appreciation, Level 0", Meta()));
            bus.Publish(new EffectsRemoved([259320], Meta(Stamp.AddSeconds(14))));
            bus.Publish(new EffectsAdded([15361], 25098977, Meta(Stamp.AddSeconds(20))));

            var replayed = new List<EffectEvent>();
            using var sub = svc.Subscribe(replayed.Add);

            replayed.Should().HaveCount(4);
            replayed[0].Kind.Should().Be(EffectEventKind.Added);
            replayed[0].State.CatalogId.Should().Be(302);
            replayed[1].Kind.Should().Be(EffectEventKind.DisplayNameChanged);
            replayed[1].State.CatalogId.Should().Be(302);
            replayed[1].State.InstanceId.Should().Be(259320);
            replayed[2].Kind.Should().Be(EffectEventKind.Removed);
            replayed[2].State.CatalogId.Should().Be(302);
            replayed[3].Kind.Should().Be(EffectEventKind.Added);
            replayed[3].State.CatalogId.Should().Be(15361);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task LateSubscribe_FromSessionStart_ThenLiveEvent_DeliversBoth_AtomicallyOrdered()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        try
        {
            await svc.StartAsync(CancellationToken.None);

            bus.Publish(new EffectsAdded([13303], 25042203, Meta()));

            var seen = new List<EffectEvent>();
            using var sub = svc.Subscribe(seen.Add);
            seen.Should().ContainSingle(
                "the existing event must replay synchronously before Subscribe returns");

            bus.Publish(new EffectsAdded([15361], 25042203, Meta(Stamp.AddSeconds(1))));

            seen.Should().HaveCount(2);
            seen[1].State.CatalogId.Should().Be(15361);
        }
        finally { await StopAsync(svc); }
    }

    // ---------- (g) Subscribe(LiveOnly) skips replay ----------

    [Fact]
    public async Task LateSubscribe_LiveOnly_DoesNotReplay_AndOnlyDeliversFutureEvents()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        try
        {
            await svc.StartAsync(CancellationToken.None);

            bus.Publish(new EffectsAdded([13303], 25042203, Meta()));

            var seen = new List<EffectEvent>();
            using var sub = svc.Subscribe(seen.Add, ReplayMode.LiveOnly);
            seen.Should().BeEmpty("LiveOnly skips the event-log replay");

            bus.Publish(new EffectsAdded([15361], 25042203, Meta(Stamp.AddSeconds(1))));

            seen.Should().ContainSingle();
            seen[0].State.CatalogId.Should().Be(15361);
        }
        finally { await StopAsync(svc); }
    }

    // ---------- Misc behaviours ----------

    [Fact]
    public async Task MultipleCatalogIds_InSingleAdd_AllFireAddedInOrder()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        var seen = new List<EffectEvent>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            await svc.StartAsync(CancellationToken.None);

            bus.Publish(new EffectsAdded([26015, 39006008, 53122008], 0, Meta()));

            seen.Should().HaveCount(3);
            seen.Select(e => e.State.CatalogId).Should().Equal(26015, 39006008, 53122008);
            seen.Should().AllSatisfy(e =>
            {
                e.Kind.Should().Be(EffectEventKind.Added);
                e.State.SourceCharId.Should().Be(0);
            });
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task DisposeSubscription_StopsFurtherEvents()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        try
        {
            await svc.StartAsync(CancellationToken.None);

            var seen = new List<EffectEvent>();
            var sub = svc.Subscribe(seen.Add, ReplayMode.LiveOnly);

            bus.Publish(new EffectsAdded([13303], 25042203, Meta()));
            seen.Should().ContainSingle();

            sub.Dispose();
            sub.Dispose(); // idempotent

            bus.Publish(new EffectsAdded([15361], 25042203, Meta(Stamp.AddSeconds(1))));
            seen.Should().ContainSingle("the disposed subscription must not receive further events");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task UnnamedEffect_RemovedBeforeUpdate_DoesNotMisroute()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        var seen = new List<EffectEvent>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            await svc.StartAsync(CancellationToken.None);

            bus.Publish(new EffectsAdded([13303], 25042203, Meta()));
            bus.Publish(new EffectsAdded([302], 25042203, Meta(Stamp.AddMinutes(1))));
            bus.Publish(new EffectNameUpdated(259320, "Performance Appreciation, Level 0", Meta(Stamp.AddMinutes(1))));

            svc.TryGet(13303, out var equip).Should().BeTrue();
            equip.InstanceId.Should().BeNull();
            equip.DisplayName.Should().BeNull();

            svc.TryGet(302, out var named).Should().BeTrue();
            named.InstanceId.Should().Be(259320);
            named.DisplayName.Should().Be("Performance Appreciation, Level 0");
        }
        finally { await StopAsync(svc); }
    }

    // ---------- Re-rename via instance-id bridge (review #593 / Finding 1) ----------

    [Fact]
    public async Task DoubleUpdate_SameInstanceId_RoutesToSameCatalog_AndFiresSecondDisplayNameChanged()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        var seen = new List<EffectEvent>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            await svc.StartAsync(CancellationToken.None);

            bus.Publish(new EffectsAdded([302], 25098977, Meta()));
            bus.Publish(new EffectNameUpdated(259320, "Performance Appreciation, Level 0", Meta()));
            bus.Publish(new EffectNameUpdated(259320, "Performance Appreciation, Level 1", Meta(Stamp.AddMinutes(2))));

            seen.Should().HaveCount(3, "Add + first-name + re-rename");
            seen[0].Kind.Should().Be(EffectEventKind.Added);
            seen[0].State.CatalogId.Should().Be(302);

            seen[1].Kind.Should().Be(EffectEventKind.DisplayNameChanged);
            seen[1].State.CatalogId.Should().Be(302);
            seen[1].State.DisplayName.Should().Be("Performance Appreciation, Level 0");

            seen[2].Kind.Should().Be(EffectEventKind.DisplayNameChanged);
            seen[2].State.CatalogId.Should().Be(302,
                "the re-rename must route via the instance-id bridge back to the SAME catalog id");
            seen[2].State.InstanceId.Should().Be(259320);
            seen[2].State.DisplayName.Should().Be("Performance Appreciation, Level 1");

            svc.TryGet(302, out var state).Should().BeTrue();
            state.DisplayName.Should().Be("Performance Appreciation, Level 1");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task DoubleUpdate_SameName_SuppressesSecondDisplayNameChanged()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        var seen = new List<EffectEvent>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            await svc.StartAsync(CancellationToken.None);

            bus.Publish(new EffectsAdded([302], 25098977, Meta()));
            bus.Publish(new EffectNameUpdated(259320, "Performance Appreciation, Level 0", Meta()));
            bus.Publish(new EffectNameUpdated(259320, "Performance Appreciation, Level 0", Meta(Stamp.AddSeconds(5))));

            seen.Should().HaveCount(2, "Add + first-name only — the same-name re-emit is suppressed");
            seen[0].Kind.Should().Be(EffectEventKind.Added);
            seen[1].Kind.Should().Be(EffectEventKind.DisplayNameChanged);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task DoubleUpdate_WithUnrelatedUnnamedPushBetween_RoutesByInstanceId_NotStack()
    {
        var bus = new TestDomainEventBus();
        var svc = NewService(bus);
        var seen = new List<EffectEvent>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            await svc.StartAsync(CancellationToken.None);

            bus.Publish(new EffectsAdded([302], 25098977, Meta()));
            bus.Publish(new EffectNameUpdated(259320, "Performance Appreciation, Level 0", Meta()));
            bus.Publish(new EffectsAdded([13303], 25098977, Meta(Stamp.AddMinutes(1))));
            bus.Publish(new EffectNameUpdated(259320, "Performance Appreciation, Level 1", Meta(Stamp.AddMinutes(2))));

            svc.TryGet(302, out var named).Should().BeTrue();
            named.InstanceId.Should().Be(259320);
            named.DisplayName.Should().Be("Performance Appreciation, Level 1");

            svc.TryGet(13303, out var equip).Should().BeTrue();
            equip.InstanceId.Should().BeNull("the stack-based pop must NOT have fired for the re-rename");
            equip.DisplayName.Should().BeNull();

            seen.Should().HaveCount(4);
            seen[3].Kind.Should().Be(EffectEventKind.DisplayNameChanged);
            seen[3].State.CatalogId.Should().Be(302,
                "the re-rename event must carry catalog 302, not the unrelated 13303 on _unnamed");
            seen[3].State.DisplayName.Should().Be("Performance Appreciation, Level 1");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Lifecycle_EmitsSubscribingDiagnostic()
    {
        var diag = new DiagnosticsSink();
        var bus = new TestDomainEventBus();
        var svc = NewService(bus, diag);
        try
        {
            await svc.StartAsync(CancellationToken.None);
            diag.Snapshot().Should().Contain(e =>
                e.Level == DiagnosticLevel.Info
                && e.Category == "GameState.Effects"
                && e.Message.Contains("Subscribing to Arda domain events"));
        }
        finally { await StopAsync(svc); }
    }

    // ---------- Plumbing ----------

    private static async Task StopAsync(PlayerEffectsStateService svc)
    {
        try { await svc.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* test cleanup */ }
        svc.Dispose();
    }
}
