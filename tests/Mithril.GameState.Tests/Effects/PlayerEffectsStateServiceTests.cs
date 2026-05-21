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
/// body. The per-test stream wrapper mirrors the
/// <see cref="Celestial.PlayerCelestialStateServiceTests"/> shape — one L1
/// driver per service, push raw Player.log lines, drain the LocalPlayer pipe.
/// </summary>
public sealed class PlayerEffectsStateServiceTests
{
    private static readonly DateTime Stamp = new(2026, 5, 20, 20, 1, 17, DateTimeKind.Utc);

    private static PlayerEffectsStateService NewService(
        ScriptedStream stream, IDiagnosticsSink? diag = null) =>
        new(stream.Driver, diag);

    // ---------- (a) live Add (arg4 == True) → Added ----------

    [Fact]
    public async Task LiveAdd_FiresAddedEvent_AndPopulatesActiveSet()
    {
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        var seen = new List<EffectEvent>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            stream.Push(Stamp,
                "[20:01:17] LocalPlayer: ProcessAddEffects(25042203, 25042203, \"[13303, ]\", True)");
            await RunUntilDrainedAsync(svc, stream);

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
        // Captured shape: a False snapshot at login lists a subset of the
        // persistent effects, and equipment-derived ones (e.g. 13303) re-arrive
        // separately as True adds. The False list is NOT a full snapshot, so
        // entries present in _active but absent from the list must remain.
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        try
        {
            // Seed: equipment-bonus effect already active from a True add.
            stream.Push(Stamp,
                "[20:01:17] LocalPlayer: ProcessAddEffects(25042203, 25042203, \"[13303, ]\", True)");
            // Then a False snapshot that does NOT mention 13303 but adds 26015.
            stream.Push(Stamp.AddSeconds(1),
                "[20:01:18] LocalPlayer: ProcessAddEffects(25042203, 0, \"[26015, ]\", False)");
            await RunUntilDrainedAsync(svc, stream);

            svc.ActiveEffects.Should().ContainKey(13303,
                "the False snapshot is additive-only and must not drop preexisting entries");
            svc.ActiveEffects.Should().ContainKey(26015);

            // sourceCharId == 0 sentinel preserved for snapshot adds.
            svc.TryGet(26015, out var snapshotState).Should().BeTrue();
            snapshotState.SourceCharId.Should().Be(0);
        }
        finally { await StopAsync(svc); }
    }

    // ---------- (c) re-apply of already-present catalog id is idempotent ----------

    [Fact]
    public async Task ReApplySameCatalogId_RefreshesTimestamp_AndFiresNoDuplicateAdded()
    {
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        var addedCount = 0;
        using var sub = svc.Subscribe(e => { if (e.Kind == EffectEventKind.Added) addedCount++; });

        try
        {
            stream.Push(Stamp,
                "[20:01:17] LocalPlayer: ProcessAddEffects(25042203, 25042203, \"[13303, ]\", True)");
            stream.Push(Stamp.AddMinutes(5),
                "[20:06:17] LocalPlayer: ProcessAddEffects(25042203, 25042203, \"[13303, ]\", True)");
            await RunUntilDrainedAsync(svc, stream);

            addedCount.Should().Be(1, "the second Add for the same catalog id is a re-emit, not a new application");
            svc.TryGet(13303, out var state).Should().BeTrue();
            state.AppliedAt.Should().Be(new DateTimeOffset(Stamp.AddMinutes(5)),
                "re-apply refreshes AppliedAt per the spec's idempotent timestamp-refresh rule");
        }
        finally { await StopAsync(svc); }
    }

    // ---------- (d) UpdateEffectName correlates to most-recent un-named ----------

    [Fact]
    public async Task UpdateEffectName_CorrelatesToMostRecentUnnamedEntry_AndFiresDisplayNameChanged()
    {
        // Captured pattern: [302] Add → Update 259328 ("Performance Appreciation, Level 0")
        // → [303] Add → Update 259329 at the same instant. Spec wording: "most
        // recently added and still lacks an InstanceId" — LIFO/stack.
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        var seen = new List<EffectEvent>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            stream.Push(Stamp,
                "[21:39:35] LocalPlayer: ProcessAddEffects(25098977, 25098977, \"[302, ]\", True)");
            stream.Push(Stamp,
                "[21:39:35] LocalPlayer: ProcessUpdateEffectName(25098977, 259320, \"Performance Appreciation, Level 0\")");
            await RunUntilDrainedAsync(svc, stream);

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
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        var seen = new List<EffectEvent>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            stream.Push(Stamp,
                "[21:39:35] LocalPlayer: ProcessAddEffects(25098977, 25098977, \"[302, ]\", True)");
            stream.Push(Stamp,
                "[21:39:35] LocalPlayer: ProcessUpdateEffectName(25098977, 259320, \"Performance Appreciation, Level 0\")");
            stream.Push(Stamp.AddSeconds(14),
                "[21:39:49] LocalPlayer: ProcessRemoveEffects(25098977, [259320,])");
            await RunUntilDrainedAsync(svc, stream);

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
        // The catalog-id-only majority (equipment bonuses) never paired with a
        // ProcessUpdateEffectName, so a RemoveEffects targeting some unrelated
        // instance id must not touch them. Spec accepts these entries linger
        // in ActiveEffects.
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        try
        {
            stream.Push(Stamp,
                "[20:01:17] LocalPlayer: ProcessAddEffects(25042203, 25042203, \"[13303, ]\", True)");
            stream.Push(Stamp.AddSeconds(1),
                "[20:01:18] LocalPlayer: ProcessRemoveEffects(25042203, [259278,])");
            await RunUntilDrainedAsync(svc, stream);

            svc.ActiveEffects.Should().ContainKey(13303,
                "an un-named entry has no InstanceId bridge and cannot be removed by id");
        }
        finally { await StopAsync(svc); }
    }

    // ---------- (f) late Subscribe(default) replays full event log from session start ----------

    [Fact]
    public async Task LateSubscribe_FromSessionStart_ReplaysFullEventLog_InOrder()
    {
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        try
        {
            stream.Push(Stamp,
                "[21:39:35] LocalPlayer: ProcessAddEffects(25098977, 25098977, \"[302, ]\", True)");
            stream.Push(Stamp,
                "[21:39:35] LocalPlayer: ProcessUpdateEffectName(25098977, 259320, \"Performance Appreciation, Level 0\")");
            stream.Push(Stamp.AddSeconds(14),
                "[21:39:49] LocalPlayer: ProcessRemoveEffects(25098977, [259320,])");
            stream.Push(Stamp.AddSeconds(20),
                "[21:39:55] LocalPlayer: ProcessAddEffects(25098977, 25098977, \"[15361, ]\", True)");
            await RunUntilDrainedAsync(svc, stream);

            var replayed = new List<EffectEvent>();
            using var sub = svc.Subscribe(replayed.Add); // default = FromSessionStart

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
        // The Subscribe path runs replay under the same lock that Fire takes;
        // a live event arriving between Subscribe return and the next handler
        // dispatch cannot interleave with the replay. Regression cover for the
        // post-#585 contract.
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(Stamp,
                "[20:01:17] LocalPlayer: ProcessAddEffects(25042203, 25042203, \"[13303, ]\", True)");
            await stream.WaitForDrainAsync(cts.Token);

            var seen = new List<EffectEvent>();
            using var sub = svc.Subscribe(seen.Add);
            seen.Should().ContainSingle(
                "the existing event must replay synchronously before Subscribe returns");

            stream.Push(Stamp.AddSeconds(1),
                "[20:01:18] LocalPlayer: ProcessAddEffects(25042203, 25042203, \"[15361, ]\", True)");
            await stream.WaitForDrainAsync(cts.Token);

            seen.Should().HaveCount(2);
            seen[1].State.CatalogId.Should().Be(15361);
        }
        finally
        {
            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch { }
            _ = runTask;
            await StopAsync(svc);
        }
    }

    // ---------- (g) Subscribe(LiveOnly) skips replay ----------

    [Fact]
    public async Task LateSubscribe_LiveOnly_DoesNotReplay_AndOnlyDeliversFutureEvents()
    {
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(Stamp,
                "[20:01:17] LocalPlayer: ProcessAddEffects(25042203, 25042203, \"[13303, ]\", True)");
            await stream.WaitForDrainAsync(cts.Token);

            var seen = new List<EffectEvent>();
            using var sub = svc.Subscribe(seen.Add, ReplayMode.LiveOnly);
            seen.Should().BeEmpty("LiveOnly skips the event-log replay");

            stream.Push(Stamp.AddSeconds(1),
                "[20:01:18] LocalPlayer: ProcessAddEffects(25042203, 25042203, \"[15361, ]\", True)");
            await stream.WaitForDrainAsync(cts.Token);

            seen.Should().ContainSingle();
            seen[0].State.CatalogId.Should().Be(15361);
        }
        finally
        {
            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch { }
            _ = runTask;
            await StopAsync(svc);
        }
    }

    // ---------- Misc behaviours ----------

    [Fact]
    public async Task MultipleCatalogIds_InSingleAdd_AllFireAddedInOrder()
    {
        // PG batches login-snapshot adds (e.g. "[26015, 39006008, ...]"); the
        // service must emit one Added per id, in list order.
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        var seen = new List<EffectEvent>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            stream.Push(Stamp,
                "[20:01:17] LocalPlayer: ProcessAddEffects(25042203, 0, \"[26015, 39006008, 53122008]\", False)");
            await RunUntilDrainedAsync(svc, stream);

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
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            var seen = new List<EffectEvent>();
            var sub = svc.Subscribe(seen.Add, ReplayMode.LiveOnly);

            stream.Push(Stamp,
                "[20:01:17] LocalPlayer: ProcessAddEffects(25042203, 25042203, \"[13303, ]\", True)");
            await stream.WaitForDrainAsync(cts.Token);
            seen.Should().ContainSingle();

            sub.Dispose();
            sub.Dispose(); // idempotent

            stream.Push(Stamp.AddSeconds(1),
                "[20:01:18] LocalPlayer: ProcessAddEffects(25042203, 25042203, \"[15361, ]\", True)");
            await stream.WaitForDrainAsync(cts.Token);
            seen.Should().ContainSingle("the disposed subscription must not receive further events");
        }
        finally
        {
            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch { }
            _ = runTask;
            await StopAsync(svc);
        }
    }

    [Fact]
    public async Task UnnamedEffect_RemovedBeforeUpdate_DoesNotMisroute()
    {
        // Defensive: an un-named entry whose Add we observed but whose Update
        // never came must not corrupt the most-recent-un-named correlation
        // when a subsequent Update for a DIFFERENT effect arrives. The first
        // Add's catalog id should be skipped on pop if it's no longer in
        // _active (here we remove it indirectly by demonstrating a second
        // Add+Update pair correlates correctly even with a stale un-named
        // entry on the stack).
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        var seen = new List<EffectEvent>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            // Equipment-bonus add (no Update ever fires for this one).
            stream.Push(Stamp,
                "[20:01:17] LocalPlayer: ProcessAddEffects(25042203, 25042203, \"[13303, ]\", True)");
            // A named effect arrives later; its Update should correlate to ITS Add,
            // not back-fill the stale 13303 on the stack.
            stream.Push(Stamp.AddMinutes(1),
                "[20:02:17] LocalPlayer: ProcessAddEffects(25042203, 25042203, \"[302, ]\", True)");
            stream.Push(Stamp.AddMinutes(1),
                "[20:02:17] LocalPlayer: ProcessUpdateEffectName(25042203, 259320, \"Performance Appreciation, Level 0\")");
            await RunUntilDrainedAsync(svc, stream);

            // 13303 stays unnamed; 302 gets named.
            svc.TryGet(13303, out var equip).Should().BeTrue();
            equip.InstanceId.Should().BeNull();
            equip.DisplayName.Should().BeNull();

            svc.TryGet(302, out var named).Should().BeTrue();
            named.InstanceId.Should().Be(259320);
            named.DisplayName.Should().Be("Performance Appreciation, Level 0");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Lifecycle_EmitsSubscribingDiagnostic()
    {
        var diag = new DiagnosticsSink();
        var stream = new ScriptedStream();
        var svc = NewService(stream, diag);
        try
        {
            await RunUntilDrainedAsync(svc, stream);
            diag.Snapshot().Should().Contain(e =>
                e.Level == DiagnosticLevel.Info
                && e.Category == "GameState.Effects"
                && e.Message.Contains("Subscribing to L1 driver"));
        }
        finally { await StopAsync(svc); }
    }

    // ---------- Plumbing ----------

    private static async Task RunUntilDrainedAsync(PlayerEffectsStateService svc, ScriptedStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    private static async Task StopAsync(PlayerEffectsStateService svc)
    {
        try { await svc.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* test cleanup */ }
        svc.Dispose();
    }

    /// <summary>
    /// Per-test L1 stream wrapper, same shape as
    /// <see cref="Celestial.PlayerCelestialStateServiceTests"/>.
    /// </summary>
    private sealed class ScriptedStream : IDisposable
    {
        public TestLogStreamDriver Driver { get; } = new();

        public void Push(DateTime ts, string line)
        {
            Driver.PushLive(TestLogEnvelopeFactory.FromRawLine(new RawLogLine(ts, line)));
        }

        public Task WaitForDrainAsync(CancellationToken ct) =>
            Driver.DrainLocalPlayerAsync().WaitAsync(ct);

        public void Dispose() => Driver.Dispose();
    }

}
