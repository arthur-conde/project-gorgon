using FluentAssertions;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Pins;
using Mithril.GameState.Tests.TestSupport;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Pins;

/// <summary>
/// L0.5 #556 Phase 3 — PlayerPinTracker now subscribes to the L1 driver's
/// unified classified pipe instead of tailing the raw Player.log stream.
/// Tests drive it via <see cref="TestLogStreamDriver"/> which pushes
/// classified envelopes (LocalPlayer or SystemSignal) into the unified
/// dispatch path that the splitter would have produced in production.
/// </summary>
public sealed class PlayerPinTrackerTests
{
    private static readonly DateTimeOffset Ts =
        new(2026, 5, 18, 10, 10, 3, TimeSpan.Zero);

    private static PlayerPinTracker NewTracker(TestLogStreamDriver driver) =>
        new(driver, new MapPinParser(), new AreaTransitionParser());

    /// <summary>
    /// Builds a SystemSignal { AreaLoading } envelope carrying the
    /// envelope-eaten <c>LOADING LEVEL …</c> payload (what the L0.5
    /// splitter produces in production for the raw line
    /// <c>[ts] LOADING LEVEL …</c>).
    /// </summary>
    private static SystemSignalLogLine AreaEnvelope(string areaKey, long seq) =>
        new(
            Timestamp: Ts,
            Kind: SystemSignalKind.AreaLoading,
            Data: $"LOADING LEVEL {areaKey}",
            Sequence: seq,
            ReadMonotonicTicks: 0);

    private static LocalPlayerLogLine PinAdd(double x, double z, string label, long seq, int b = 0, int c = 0) =>
        new(
            Timestamp: Ts,
            Data: $"ProcessMapPinAdd(1, {b}, {c}, ({x:0.00}, 0.00, {z:0.00}), \"{label}\")",
            Sequence: seq,
            ReadMonotonicTicks: 0);

    private static LocalPlayerLogLine PinRemove(double x, double z, string label, long seq) =>
        new(
            Timestamp: Ts,
            Data: $"ProcessMapPinRemove(1, 0, 0, ({x:0.00}, 0.00, {z:0.00}), \"{label}\")",
            Sequence: seq,
            ReadMonotonicTicks: 0);

    [Fact]
    public async Task Add_populates_current_area_pins_with_decoded_appearance()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));
        driver.PushReplay(PinAdd(1425.06, 2924.99, "South", seq: 2, b: 1, c: 1));
        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            svc.CurrentArea.Should().Be("AreaSerbule");
            var pin = svc.CurrentAreaPins.Should().ContainSingle().Subject;
            pin.Label.Should().Be("South");
            pin.X.Should().Be(1425.06);
            pin.Z.Should().Be(2924.99);
            pin.Shape.Should().Be(PinShape.Square);
            pin.Color.Should().Be(PinColor.Red);
            pin.Appearance.Should().Be("red square");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Replay_burst_is_idempotent_no_duplicates_no_extra_events()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));
        driver.PushReplay(PinAdd(10, 20, "A", seq: 2));
        driver.PushReplay(PinAdd(30, 40, "B", seq: 3));
        // Login/area-entry re-emits the whole set verbatim.
        driver.PushReplay(PinAdd(10, 20, "A", seq: 4));
        driver.PushReplay(PinAdd(30, 40, "B", seq: 5));

        var svc = NewTracker(driver);
        try
        {
            var seen = new List<PinSetChanged>();
            using var sub = svc.Subscribe(seen.Add);

            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            svc.CurrentAreaPins.Should().HaveCount(2);
            // 1 Snapshot from Subscribe (above) + 2 Adds (idempotent re-adds
            // produce no notification).
            seen.Count(n => n.Kind == PinSetChange.Added).Should().Be(2);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Remove_deletes_by_coordinate()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));
        driver.PushReplay(PinAdd(10, 20, "A", seq: 2));
        driver.PushReplay(PinRemove(10, 20, "A", seq: 3));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            svc.CurrentAreaPins.Should().BeEmpty();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Rename_is_remove_then_add_at_same_coord()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));
        driver.PushReplay(PinAdd(10, 20, "", seq: 2));
        driver.PushReplay(PinRemove(10, 20, "", seq: 3));
        driver.PushReplay(PinAdd(10, 20, "Calib 1", seq: 4));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            svc.CurrentAreaPins.Should().ContainSingle()
                .Which.Label.Should().Be("Calib 1");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Area_change_swaps_the_set_and_raises_AreaChanged()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));
        driver.PushReplay(PinAdd(10, 20, "A", seq: 2));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            var seen = new List<PinSetChanged>();
            using var sub = svc.Subscribe(seen.Add); // Snapshot replay first

            driver.PushLive(AreaEnvelope("AreaEltibule", seq: 3));
            await driver.DrainClassifiedAsync();

            svc.CurrentArea.Should().Be("AreaEltibule");
            svc.CurrentAreaPins.Should().BeEmpty();
            seen.Should().Contain(n => n.Kind == PinSetChange.AreaChanged && n.Area == "AreaEltibule");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_replays_snapshot_synchronously_then_delivers_live()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));
        driver.PushReplay(PinAdd(10, 20, "A", seq: 2));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            var seen = new List<PinSetChanged>();
            using var sub = svc.Subscribe(seen.Add);
            seen.Should().ContainSingle()
                .Which.Should().Match<PinSetChanged>(n =>
                    n.Kind == PinSetChange.Snapshot && n.Pins.Count == 1);

            driver.PushLive(PinAdd(30, 40, "B", seq: 3));
            await driver.DrainClassifiedAsync();

            var added = seen.Last();
            added.Kind.Should().Be(PinSetChange.Added);
            added.Pin!.Label.Should().Be("B");
            added.ObservedAt.Offset.Should().Be(TimeSpan.Zero);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Cross_pipe_ordering_AreaLoading_precedes_PinBurst_no_stale_drop()
    {
        // The unified-pipe race-fix property (#556 §1) — Pin tracker
        // observes the LOADING LEVEL envelope BEFORE the pin replay burst
        // for that area, so the ReconcileArea() drop fires first and the
        // subsequent pins land in the *new* area's set rather than being
        // wiped by a late-arriving area transition.
        using var driver = new TestLogStreamDriver();
        // Initial area + one pin.
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));
        driver.PushReplay(PinAdd(100, 200, "OLD", seq: 2));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();
            svc.CurrentArea.Should().Be("AreaSerbule");
            svc.CurrentAreaPins.Should().ContainSingle().Which.Label.Should().Be("OLD");

            // Live zone change burst: LOADING LEVEL followed immediately
            // by the new area's pin-add. On the unified pipe these arrive
            // in source-Sequence order through the single subscription,
            // so AreaLoading is observed first.
            driver.PushLive(AreaEnvelope("AreaEltibule", seq: 3));
            driver.PushLive(PinAdd(500, 600, "NEW", seq: 4));
            await driver.DrainClassifiedAsync();

            svc.CurrentArea.Should().Be("AreaEltibule");
            svc.CurrentAreaPins.Should().ContainSingle().Which.Label.Should().Be("NEW");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_snapshot_stamp_is_last_envelope_timestamp_not_wall_clock()
    {
        // Principle 13 — world-event-driven paths must not leak wall clock
        // into derived state. Pre-#715 the synthetic Snapshot notification
        // emitted by Subscribe stamped its ObservedAt with
        // DateTimeOffset.UtcNow, so two subscribers attaching at different
        // real-elapsed times observed different timestamps on the same
        // tracker state. Post-#715 the stamp is the most-recent envelope
        // timestamp the tracker has applied — identical across late
        // subscribers regardless of when they attach.
        var areaTs = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var pinTs = areaTs.AddMinutes(1);

        using var driver = new TestLogStreamDriver();
        driver.PushReplay(new SystemSignalLogLine(
            Timestamp: areaTs,
            Kind: SystemSignalKind.AreaLoading,
            Data: "LOADING LEVEL AreaSerbule",
            Sequence: 1,
            ReadMonotonicTicks: 0));
        driver.PushReplay(new LocalPlayerLogLine(
            Timestamp: pinTs,
            Data: "ProcessMapPinAdd(1, 0, 0, (10.00, 0.00, 20.00), \"A\")",
            Sequence: 2,
            ReadMonotonicTicks: 0));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            // Real-elapsed gap between tracker reaching steady state and
            // subscription attaching. Under the old wall-clock stamp the
            // Snapshot's ObservedAt would slip past pinTs by at least this
            // much; under the envelope-stamp fix it stays anchored to pinTs.
            await Task.Delay(TimeSpan.FromMilliseconds(50));

            var seenA = new List<PinSetChanged>();
            using var subA = svc.Subscribe(seenA.Add);

            await Task.Delay(TimeSpan.FromMilliseconds(50));

            var seenB = new List<PinSetChanged>();
            using var subB = svc.Subscribe(seenB.Add);

            seenA.Should().ContainSingle()
                .Which.Kind.Should().Be(PinSetChange.Snapshot);
            seenB.Should().ContainSingle()
                .Which.Kind.Should().Be(PinSetChange.Snapshot);

            seenA[0].ObservedAt.Should().Be(pinTs,
                "the Snapshot stamp must come from the most-recent envelope the tracker applied, not wall clock");
            seenB[0].ObservedAt.Should().Be(seenA[0].ObservedAt,
                "late subscribers observing the same tracker state must see identical snapshot timestamps");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Area_change_notification_uses_envelope_timestamp_not_wall_clock()
    {
        // The other half of the principle-13 fix for ReconcileArea — the
        // AreaChanged notification's ObservedAt now comes from the
        // triggering LOADING LEVEL envelope, not DateTimeOffset.UtcNow.
        // Test pushes two area envelopes at distinct fixed-past timestamps;
        // each AreaChanged notification carries its envelope's timestamp.
        var firstAreaTs = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var secondAreaTs = firstAreaTs.AddMinutes(7);

        using var driver = new TestLogStreamDriver();
        driver.PushReplay(new SystemSignalLogLine(
            Timestamp: firstAreaTs,
            Kind: SystemSignalKind.AreaLoading,
            Data: "LOADING LEVEL AreaSerbule",
            Sequence: 1,
            ReadMonotonicTicks: 0));

        var svc = NewTracker(driver);
        try
        {
            var seen = new List<PinSetChanged>();
            using var sub = svc.Subscribe(seen.Add);

            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            // Real-elapsed gap then push a second area envelope at a
            // separate fixed timestamp.
            await Task.Delay(TimeSpan.FromMilliseconds(50));

            driver.PushLive(new SystemSignalLogLine(
                Timestamp: secondAreaTs,
                Kind: SystemSignalKind.AreaLoading,
                Data: "LOADING LEVEL AreaEltibule",
                Sequence: 2,
                ReadMonotonicTicks: 0));
            await driver.DrainClassifiedAsync();

            var areaChanges = seen.Where(n => n.Kind == PinSetChange.AreaChanged).ToList();
            areaChanges.Should().HaveCount(2,
                "one AreaChanged per envelope (initial replay + the live push)");
            areaChanges[0].ObservedAt.Should().Be(firstAreaTs,
                "ReconcileArea must stamp the AreaChanged note with the triggering envelope's timestamp, not wall clock");
            areaChanges[1].ObservedAt.Should().Be(secondAreaTs);
        }
        finally { await StopAsync(svc); }
    }

    private static async Task StartAsync(PlayerPinTracker svc)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
    }

    private static async Task StopAsync(PlayerPinTracker svc)
    {
        try { await svc.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* test cleanup */ }
        svc.Dispose();
    }
}
