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
