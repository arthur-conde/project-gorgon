using FluentAssertions;
using Mithril.GameState.Gifting;
using Mithril.GameState.Tests.TestSupport;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Gifting;

/// <summary>
/// Acceptance suite for <see cref="GiftSignalService"/> per issue
/// <a href="https://github.com/moumantai-gg/mithril/issues/596">#596</a>.
/// Mirrors the <c>PlayerCelestialStateServiceTests</c> shape
/// (per-test L1 driver, push raw Player.log lines, drain the LocalPlayer
/// pipe) and exhausts the issue's <i>Test plan</i> checklist:
///
/// <list type="bullet">
///   <item>Happy path (DeleteItem → DeltaFavor).</item>
///   <item>Reverse order (DeltaFavor → DeleteItem).</item>
///   <item>NpcKey mismatch on DeltaFavor → no emission.</item>
///   <item>DeleteItem outside an active interaction → no emission.</item>
///   <item>Multiple gifts in one interaction.</item>
///   <item>EndInteraction clears the window.</item>
///   <item>Late-subscriber FromSessionStart replay.</item>
///   <item>LiveOnly subscribe skips replay.</item>
///   <item>DeleteItem for an unknown instanceId (no AddItem) → no emission.</item>
///   <item>Recorded-session replay against the captured NPC_Way 2026-05-20 fixture.</item>
/// </list>
/// </summary>
public sealed class GiftSignalServiceTests
{
    private static readonly DateTime Stamp = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    // ---------- Happy path ----------

    [Fact]
    public async Task DeleteThenFavor_EmitsGiftAccepted_WithCorrectFields()
    {
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver);
        var seen = new List<GiftAccepted>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessAddItem(RubberyTongue(42), -1, True)");
            stream.Push(Stamp.AddSeconds(1),
                "[12:00:01] LocalPlayer: ProcessStartInteraction(8887, 7, 100.0, True, \"NPC_Way\")");
            stream.Push(Stamp.AddSeconds(2),
                "[12:00:02] LocalPlayer: ProcessDeleteItem(42)");
            stream.Push(Stamp.AddSeconds(3),
                "[12:00:03] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 6.72, True)");
            await RunUntilDrainedAsync(svc, stream);

            seen.Should().ContainSingle();
            var evt = seen[0];
            evt.NpcKey.Should().Be("NPC_Way");
            evt.ItemInstanceId.Should().Be(42);
            evt.ItemInternalName.Should().Be("RubberyTongue");
            evt.DeltaFavor.Should().Be(6.72);
            evt.Timestamp.Should().Be(new DateTimeOffset(Stamp.AddSeconds(3)),
                "resolve point is the DeltaFavor log-line timestamp");
            evt.InteractionStartedAt.Should().Be(new DateTimeOffset(Stamp.AddSeconds(1)),
                "StartInteraction timestamp threads through to the resolved event");
        }
        finally { await StopAsync(svc); }
    }

    // ---------- Reverse order ----------

    [Fact]
    public async Task FavorThenDelete_EmitsIdenticalGiftAccepted()
    {
        // Both arrival orders must produce one GiftAccepted with the same
        // content — the SM's existing both-orders support is what this
        // verifies survived the lift. The DeltaFavor timestamp wins regardless
        // of which half arrived first, matching CalibrationService.cs:195.
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver);
        var seen = new List<GiftAccepted>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessAddItem(RubberyTongue(42), -1, True)");
            stream.Push(Stamp.AddSeconds(1),
                "[12:00:01] LocalPlayer: ProcessStartInteraction(8887, 7, 100.0, True, \"NPC_Way\")");
            stream.Push(Stamp.AddSeconds(2),
                "[12:00:02] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 6.72, True)");
            stream.Push(Stamp.AddSeconds(3),
                "[12:00:03] LocalPlayer: ProcessDeleteItem(42)");
            await RunUntilDrainedAsync(svc, stream);

            seen.Should().ContainSingle();
            var evt = seen[0];
            evt.NpcKey.Should().Be("NPC_Way");
            evt.ItemInstanceId.Should().Be(42);
            evt.ItemInternalName.Should().Be("RubberyTongue");
            evt.DeltaFavor.Should().Be(6.72);
            evt.Timestamp.Should().Be(new DateTimeOffset(Stamp.AddSeconds(2)),
                "DeltaFavor's timestamp wins regardless of arrival order");
        }
        finally { await StopAsync(svc); }
    }

    // ---------- NpcKey mismatch ----------

    [Fact]
    public async Task DeltaFavor_ForDifferentNpcKey_DoesNotEmit()
    {
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver);
        var seen = new List<GiftAccepted>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessAddItem(RubberyTongue(42), -1, True)");
            stream.Push(Stamp.AddSeconds(1),
                "[12:00:01] LocalPlayer: ProcessStartInteraction(8887, 7, 100.0, True, \"NPC_Way\")");
            stream.Push(Stamp.AddSeconds(2),
                "[12:00:02] LocalPlayer: ProcessDeleteItem(42)");
            // DeltaFavor for an entirely different NPC — must be ignored, the
            // pending DeleteItem must remain unresolved.
            stream.Push(Stamp.AddSeconds(3),
                "[12:00:03] LocalPlayer: ProcessDeltaFavor(9999, \"NPC_Else\", 6.72, True)");
            await RunUntilDrainedAsync(svc, stream);

            seen.Should().BeEmpty();
        }
        finally { await StopAsync(svc); }
    }

    // ---------- No active interaction ----------

    [Fact]
    public async Task DeleteItem_OutsideInteraction_DoesNotEmit_AndDoesNotLeakIntoNextInteraction()
    {
        // Without an armed window, DeleteItem must be ignored — and crucially,
        // _pendingDeletedItem must not carry across a future StartInteraction
        // so a subsequent matching DeltaFavor for the second NPC can't
        // mis-claim the orphan delete.
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver);
        var seen = new List<GiftAccepted>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessAddItem(RubberyTongue(42), -1, True)");
            // Orphan delete — no active interaction.
            stream.Push(Stamp.AddSeconds(1),
                "[12:00:01] LocalPlayer: ProcessDeleteItem(42)");
            // Now start an interaction and emit a positive DeltaFavor —
            // without a matching DeleteItem inside this window, no event
            // fires.
            stream.Push(Stamp.AddSeconds(2),
                "[12:00:02] LocalPlayer: ProcessStartInteraction(8887, 7, 100.0, True, \"NPC_Way\")");
            stream.Push(Stamp.AddSeconds(3),
                "[12:00:03] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 6.72, True)");
            await RunUntilDrainedAsync(svc, stream);

            seen.Should().BeEmpty(
                "the orphan delete must not carry across into the next interaction window");
        }
        finally { await StopAsync(svc); }
    }

    // ---------- Multiple gifts in one interaction ----------

    [Fact]
    public async Task TwoGiftsInOneInteraction_EmitTwoEvents_WithCorrectPerGiftFields()
    {
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver);
        var seen = new List<GiftAccepted>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessAddItem(RubberyTongue(42), -1, True)");
            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessAddItem(Painting4A(99), -1, True)");
            stream.Push(Stamp.AddSeconds(1),
                "[12:00:01] LocalPlayer: ProcessStartInteraction(8887, 7, 100.0, True, \"NPC_Way\")");
            stream.Push(Stamp.AddSeconds(2),
                "[12:00:02] LocalPlayer: ProcessDeleteItem(42)");
            stream.Push(Stamp.AddSeconds(3),
                "[12:00:03] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 6.72, True)");
            stream.Push(Stamp.AddSeconds(4),
                "[12:00:04] LocalPlayer: ProcessDeleteItem(99)");
            stream.Push(Stamp.AddSeconds(5),
                "[12:00:05] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 1.0752, True)");
            await RunUntilDrainedAsync(svc, stream);

            seen.Should().HaveCount(2);
            seen[0].ItemInstanceId.Should().Be(42);
            seen[0].ItemInternalName.Should().Be("RubberyTongue");
            seen[0].DeltaFavor.Should().Be(6.72);
            seen[1].ItemInstanceId.Should().Be(99);
            seen[1].ItemInternalName.Should().Be("Painting4A");
            seen[1].DeltaFavor.Should().Be(1.0752);
            // Both gifts share the same StartInteraction timestamp.
            seen.Should().AllSatisfy(e =>
                e.InteractionStartedAt.Should().Be(new DateTimeOffset(Stamp.AddSeconds(1))));
        }
        finally { await StopAsync(svc); }
    }

    // ---------- EndInteraction clears ----------

    [Fact]
    public async Task EndInteraction_ClosesWindow_NoEmissionForLaterDelete()
    {
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver);
        var seen = new List<GiftAccepted>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessAddItem(RubberyTongue(42), -1, True)");
            stream.Push(Stamp.AddSeconds(1),
                "[12:00:01] LocalPlayer: ProcessStartInteraction(8887, 7, 100.0, True, \"NPC_Way\")");
            stream.Push(Stamp.AddSeconds(2),
                "[12:00:02] LocalPlayer: ProcessEndInteraction(8887)");
            // Stragglers after EndInteraction: window is closed → ignored.
            stream.Push(Stamp.AddSeconds(3),
                "[12:00:03] LocalPlayer: ProcessDeleteItem(42)");
            stream.Push(Stamp.AddSeconds(4),
                "[12:00:04] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 6.72, True)");
            await RunUntilDrainedAsync(svc, stream);

            seen.Should().BeEmpty();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task EndInteraction_ForUnrelatedEntity_DoesNotClearActiveWindow()
    {
        // An EndInteraction for an entity we never armed on (e.g. a graveyard
        // interaction the SM didn't track) must NOT stomp the active
        // gift-eligible window. Defensive against PG sequences where short
        // unrelated interactions interleave with a longer favor session.
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver);
        var seen = new List<GiftAccepted>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessAddItem(RubberyTongue(42), -1, True)");
            stream.Push(Stamp.AddSeconds(1),
                "[12:00:01] LocalPlayer: ProcessStartInteraction(8887, 7, 100.0, True, \"NPC_Way\")");
            // Unrelated EndInteraction — different entity id, must NOT clear.
            stream.Push(Stamp.AddSeconds(2),
                "[12:00:02] LocalPlayer: ProcessEndInteraction(25237464)");
            stream.Push(Stamp.AddSeconds(3),
                "[12:00:03] LocalPlayer: ProcessDeleteItem(42)");
            stream.Push(Stamp.AddSeconds(4),
                "[12:00:04] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 6.72, True)");
            await RunUntilDrainedAsync(svc, stream);

            seen.Should().ContainSingle();
            seen[0].NpcKey.Should().Be("NPC_Way");
        }
        finally { await StopAsync(svc); }
    }

    // ---------- DeleteItem for unknown instanceId ----------

    [Fact]
    public async Task DeleteItem_NoPriorAddItem_DoesNotEmit_AndDoesNotMutateMap()
    {
        // If the service's instanceId map doesn't know the id (because no
        // ProcessAddItem ingested it — e.g. it pre-dates the session log
        // window), we can't resolve InternalName. Drop the line with a
        // Trace and skip; don't half-arm _pendingDeletedItem with an empty
        // name.
        var diag = new DiagnosticsSink();
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver, diag);
        var seen = new List<GiftAccepted>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessStartInteraction(8887, 7, 100.0, True, \"NPC_Way\")");
            stream.Push(Stamp.AddSeconds(1),
                "[12:00:01] LocalPlayer: ProcessDeleteItem(99999)");
            stream.Push(Stamp.AddSeconds(2),
                "[12:00:02] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 6.72, True)");
            await RunUntilDrainedAsync(svc, stream);

            seen.Should().BeEmpty();
            diag.Snapshot().Should().Contain(e =>
                e.Category == "GameState.Gifting"
                && e.Message.Contains("unresolved"));
        }
        finally { await StopAsync(svc); }
    }

    // ---------- Late subscriber: FromSessionStart replay ----------

    [Fact]
    public async Task LateSubscribe_FromSessionStart_ReplaysFullEventLog_InOrder()
    {
        // The post-#585 React-channel contract: a consumer attaching after
        // the L1 driver has already drained must receive every GiftAccepted
        // emitted in this session, in original order, before any live event.
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver);
        try
        {
            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessAddItem(RubberyTongue(42), -1, True)");
            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessAddItem(Painting4A(99), -1, True)");
            stream.Push(Stamp.AddSeconds(1),
                "[12:00:01] LocalPlayer: ProcessStartInteraction(8887, 7, 100.0, True, \"NPC_Way\")");
            stream.Push(Stamp.AddSeconds(2),
                "[12:00:02] LocalPlayer: ProcessDeleteItem(42)");
            stream.Push(Stamp.AddSeconds(3),
                "[12:00:03] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 6.72, True)");
            stream.Push(Stamp.AddSeconds(4),
                "[12:00:04] LocalPlayer: ProcessDeleteItem(99)");
            stream.Push(Stamp.AddSeconds(5),
                "[12:00:05] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 1.0752, True)");
            await RunUntilDrainedAsync(svc, stream);

            var replayed = new List<GiftAccepted>();
            using var sub = svc.Subscribe(replayed.Add);

            replayed.Should().HaveCount(2);
            replayed[0].ItemInstanceId.Should().Be(42);
            replayed[1].ItemInstanceId.Should().Be(99);
        }
        finally { await StopAsync(svc); }
    }

    // ---------- Live-only subscriber ----------

    [Fact]
    public async Task LateSubscribe_LiveOnly_DoesNotReplay_AndOnlyDeliversFutureEvents()
    {
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            // Pre-attach: fire one gift.
            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessAddItem(RubberyTongue(42), -1, True)");
            stream.Push(Stamp.AddSeconds(1),
                "[12:00:01] LocalPlayer: ProcessStartInteraction(8887, 7, 100.0, True, \"NPC_Way\")");
            stream.Push(Stamp.AddSeconds(2),
                "[12:00:02] LocalPlayer: ProcessDeleteItem(42)");
            stream.Push(Stamp.AddSeconds(3),
                "[12:00:03] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 6.72, True)");
            await stream.WaitForDrainAsync(cts.Token);

            var seen = new List<GiftAccepted>();
            using var sub = svc.Subscribe(seen.Add, ReplayMode.LiveOnly);
            seen.Should().BeEmpty("LiveOnly skips the event-log replay");

            // Fire a second gift after attach; only this one should land.
            stream.Push(Stamp.AddSeconds(4),
                "[12:00:04] LocalPlayer: ProcessAddItem(Painting4A(99), -1, True)");
            stream.Push(Stamp.AddSeconds(5),
                "[12:00:05] LocalPlayer: ProcessDeleteItem(99)");
            stream.Push(Stamp.AddSeconds(6),
                "[12:00:06] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 1.0752, True)");
            await stream.WaitForDrainAsync(cts.Token);

            seen.Should().ContainSingle();
            seen[0].ItemInstanceId.Should().Be(99);
        }
        finally
        {
            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch { }
            _ = runTask;
            await StopAsync(svc);
        }
    }

    // ---------- Live event arriving between Subscribe and Push ----------

    [Fact]
    public async Task LateSubscribe_FromSessionStart_ThenLiveGift_DeliversBothAtomicallyOrdered()
    {
        // The Subscribe path runs replay under the same lock Emit takes; a
        // live gift arriving between Subscribe return and the next handler
        // dispatch cannot interleave with the replay. Same shape as
        // PlayerEffectsStateServiceTests.LateSubscribe_FromSessionStart_ThenLiveEvent_DeliversBoth_AtomicallyOrdered.
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessAddItem(RubberyTongue(42), -1, True)");
            stream.Push(Stamp.AddSeconds(1),
                "[12:00:01] LocalPlayer: ProcessStartInteraction(8887, 7, 100.0, True, \"NPC_Way\")");
            stream.Push(Stamp.AddSeconds(2),
                "[12:00:02] LocalPlayer: ProcessDeleteItem(42)");
            stream.Push(Stamp.AddSeconds(3),
                "[12:00:03] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 6.72, True)");
            await stream.WaitForDrainAsync(cts.Token);

            var seen = new List<GiftAccepted>();
            using var sub = svc.Subscribe(seen.Add); // default = FromSessionStart
            seen.Should().ContainSingle(
                "the pre-existing gift must replay synchronously before Subscribe returns");

            stream.Push(Stamp.AddSeconds(4),
                "[12:00:04] LocalPlayer: ProcessAddItem(Painting4A(99), -1, True)");
            stream.Push(Stamp.AddSeconds(5),
                "[12:00:05] LocalPlayer: ProcessDeleteItem(99)");
            stream.Push(Stamp.AddSeconds(6),
                "[12:00:06] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 1.0752, True)");
            await stream.WaitForDrainAsync(cts.Token);

            seen.Should().HaveCount(2);
            seen[0].ItemInstanceId.Should().Be(42);
            seen[1].ItemInstanceId.Should().Be(99);
        }
        finally
        {
            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch { }
            _ = runTask;
            await StopAsync(svc);
        }
    }

    // ---------- Dispose ----------

    [Fact]
    public async Task DisposeSubscription_StopsFurtherEvents()
    {
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            var seen = new List<GiftAccepted>();
            var sub = svc.Subscribe(seen.Add, ReplayMode.LiveOnly);

            stream.Push(Stamp,
                "[12:00:00] LocalPlayer: ProcessAddItem(RubberyTongue(42), -1, True)");
            stream.Push(Stamp.AddSeconds(1),
                "[12:00:01] LocalPlayer: ProcessStartInteraction(8887, 7, 100.0, True, \"NPC_Way\")");
            stream.Push(Stamp.AddSeconds(2),
                "[12:00:02] LocalPlayer: ProcessDeleteItem(42)");
            stream.Push(Stamp.AddSeconds(3),
                "[12:00:03] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 6.72, True)");
            await stream.WaitForDrainAsync(cts.Token);
            seen.Should().ContainSingle();

            sub.Dispose();
            sub.Dispose(); // idempotent

            stream.Push(Stamp.AddSeconds(4),
                "[12:00:04] LocalPlayer: ProcessAddItem(Painting4A(99), -1, True)");
            stream.Push(Stamp.AddSeconds(5),
                "[12:00:05] LocalPlayer: ProcessDeleteItem(99)");
            stream.Push(Stamp.AddSeconds(6),
                "[12:00:06] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 1.0752, True)");
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

    // ---------- Recorded-session replay ----------

    [Fact]
    public async Task RecordedSession_NpcWayCapture_EmitsThreeGifts_PreservingInterloperQuirk()
    {
        // From I:/src/mithril-logs/sessions/2026-05/Player-2026-05-20-0400.log
        // at 02:09:31 (AddItem re-emit on zone entry) through 02:12:21 (final
        // gift). The 02:12:18 ProcessDeltaFavor(84) is a non-gift interloper
        // (likely a quest-turnin landing right at talk-open); the SM lifted
        // verbatim from CalibrationService claims it for the first DeleteItem
        // — see issue #596's "Out of scope → SM behavioural fixes" note. This
        // test PINS the existing behaviour: any future SM refinement that
        // changes attribution updates this assertion deliberately, not by
        // accident.
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver);
        var seen = new List<GiftAccepted>();
        using var sub = svc.Subscribe(seen.Add);

        try
        {
            // ProcessAddItem re-emit burst at zone entry (02:09:31).
            stream.Push(new DateTime(2026, 5, 20, 2, 9, 31, DateTimeKind.Utc),
                "[02:09:31] LocalPlayer: ProcessAddItem(RubberyTongue(134693667), -1, False)");
            stream.Push(new DateTime(2026, 5, 20, 2, 9, 31, DateTimeKind.Utc),
                "[02:09:31] LocalPlayer: ProcessAddItem(Painting4A(134810549), -1, False)");
            stream.Push(new DateTime(2026, 5, 20, 2, 9, 31, DateTimeKind.Utc),
                "[02:09:31] LocalPlayer: ProcessAddItem(PieceOfGreenGlass(135331202), -1, False)");

            // StartInteraction at 02:12:14.
            stream.Push(new DateTime(2026, 5, 20, 2, 12, 14, DateTimeKind.Utc),
                "[02:12:14] LocalPlayer: ProcessStartInteraction(8887, 7, 761.9012, True, \"NPC_Way\")");

            // The interloper DeltaFavor at 02:12:18 (likely quest reward).
            stream.Push(new DateTime(2026, 5, 20, 2, 12, 18, DateTimeKind.Utc),
                "[02:12:18] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 84, True)");
            // First DeleteItem at 02:12:18 — claims the interloper delta.
            stream.Push(new DateTime(2026, 5, 20, 2, 12, 18, DateTimeKind.Utc),
                "[02:12:18] LocalPlayer: ProcessDeleteItem(134693667)");

            // Gift 1's actual outcome at 02:12:20.
            stream.Push(new DateTime(2026, 5, 20, 2, 12, 20, DateTimeKind.Utc),
                "[02:12:20] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 6.72, True)");
            // Second DeleteItem at 02:12:20.
            stream.Push(new DateTime(2026, 5, 20, 2, 12, 20, DateTimeKind.Utc),
                "[02:12:20] LocalPlayer: ProcessDeleteItem(134810549)");

            // Final DeltaFavor + DeleteItem pair at 02:12:21.
            stream.Push(new DateTime(2026, 5, 20, 2, 12, 21, DateTimeKind.Utc),
                "[02:12:21] LocalPlayer: ProcessDeltaFavor(8887, \"NPC_Way\", 1.0752, True)");
            stream.Push(new DateTime(2026, 5, 20, 2, 12, 21, DateTimeKind.Utc),
                "[02:12:21] LocalPlayer: ProcessDeleteItem(135331202)");

            await RunUntilDrainedAsync(svc, stream);

            seen.Should().HaveCount(3,
                "the SM emits three GiftAccepted events for the three DeleteItem verbs in the active interaction");

            // Preserved-quirk pin: the interloper +84 is claimed by the first
            // gift (RubberyTongue). Real game truth is that the +84 was
            // probably a quest reward and RubberyTongue actually gave +6.72,
            // but the lifted SM doesn't distinguish — this is the bug
            // documented in #596's "SM behavioural fixes" out-of-scope note.
            seen[0].NpcKey.Should().Be("NPC_Way");
            seen[0].ItemInstanceId.Should().Be(134693667);
            seen[0].ItemInternalName.Should().Be("RubberyTongue");
            seen[0].DeltaFavor.Should().Be(84,
                "preserved-quirk pin: the interloper delta is claimed by the first DeleteItem");

            seen[1].ItemInstanceId.Should().Be(134810549);
            seen[1].ItemInternalName.Should().Be("Painting4A");
            seen[1].DeltaFavor.Should().Be(6.72);

            seen[2].ItemInstanceId.Should().Be(135331202);
            seen[2].ItemInternalName.Should().Be("PieceOfGreenGlass");
            seen[2].DeltaFavor.Should().Be(1.0752);

            // All three share the same StartInteraction stamp.
            seen.Should().AllSatisfy(e =>
                e.InteractionStartedAt.Should().Be(
                    new DateTimeOffset(new DateTime(2026, 5, 20, 2, 12, 14, DateTimeKind.Utc))));
        }
        finally { await StopAsync(svc); }
    }

    // ---------- Lifecycle diagnostic ----------

    [Fact]
    public async Task Lifecycle_EmitsSubscribingDiagnostic()
    {
        var diag = new DiagnosticsSink();
        var stream = new ScriptedStream();
        var svc = new GiftSignalService(stream.Driver, diag);
        try
        {
            await RunUntilDrainedAsync(svc, stream);
            diag.Snapshot().Should().Contain(e =>
                e.Level == DiagnosticLevel.Info
                && e.Category == "GameState.Gifting"
                && e.Message.Contains("Subscribing to L1 driver"));
        }
        finally { await StopAsync(svc); }
    }

    // ---------- Plumbing ----------

    private static async Task RunUntilDrainedAsync(GiftSignalService svc, ScriptedStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    private static async Task StopAsync(GiftSignalService svc)
    {
        try { await svc.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* test cleanup */ }
        svc.Dispose();
    }

    /// <summary>
    /// Per-test L1 stream wrapper, matching the
    /// <c>PlayerCelestialStateServiceTests</c> shape.
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
