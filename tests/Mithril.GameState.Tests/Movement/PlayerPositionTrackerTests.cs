using FluentAssertions;
using Mithril.GameState.Movement;
using Mithril.GameState.Tests.TestSupport;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Movement;

/// <summary>
/// L0.5 #556 Phase 3 — PlayerPositionTracker now subscribes to the L1
/// driver's unified classified pipe.
/// <list type="bullet">
///   <item><c>ProcessNewPosition</c> arrives as
///   <see cref="LocalPlayerLogLine"/> (the LocalPlayer typed pipe).</item>
///   <item><c>ProcessAddPlayer</c> arrives as
///   <see cref="SystemSignalLogLine"/> { <see cref="SystemSignalKind.PlayerAdded"/> }
///   — Risk-4 carve-out from #556 (L0.5 routes the local-player spawn line
///   to the system-signal pipe because it's a session-lifecycle event,
///   not a verb on the LocalPlayer pipe).</item>
/// </list>
/// Both reach the tracker on the same unified subscription in source-Sequence
/// order; the tracker's <c>switch</c> dispatches them to the right parse path.
/// </summary>
public sealed class PlayerPositionTrackerTests
{
    private static readonly DateTimeOffset Ts =
        new(2026, 5, 18, 10, 45, 47, TimeSpan.Zero);

    // Envelope-eaten payloads (what L0.5 produces in production — the
    // [ts] LocalPlayer: prefix is stripped by the classifier).
    private const string PosData =
        "ProcessNewPosition((834.09, 290.24, 3480.81), (0,0,0,1), Walk, OnLand, UseTeleportationCircle, Looping, 0, False, True, 1, 2)";
    private const string PosData2 =
        "ProcessNewPosition((-790.06, 309.18, -3386.07), (0,0,0,1), Run, OnLand, Zone, Looping, 0, False, True, 1, 2)";
    private const string SpawnData =
        "ProcessAddPlayer(1, 2, \"@Base2-m(sex=m;Face=@eq(a=1))\", \"Emraell\", \"A player!\", System.String[], (1522.22, 112.27, 288.13), (0,0,0,1), Idle, Standing, 0, 0, True)";

    private static PlayerPositionTracker NewTracker(TestLogStreamDriver driver) =>
        new(driver, new PlayerPositionParser());

    private static LocalPlayerLogLine LocalEnvelope(string data, long seq, DateTimeOffset? at = null) =>
        new(
            Timestamp: at ?? Ts,
            Data: data,
            Sequence: seq,
            ReadMonotonicTicks: 0);

    private static SystemSignalLogLine SpawnEnvelope(string data, long seq, DateTimeOffset? at = null) =>
        new(
            Timestamp: at ?? Ts,
            Kind: SystemSignalKind.PlayerAdded,
            Data: data,
            Sequence: seq,
            ReadMonotonicTicks: 0);

    [Fact]
    public async Task Position_line_populates_Current_with_coords_and_utc_instant()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(LocalEnvelope(PosData, seq: 1));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            svc.Current.Should().NotBeNull();
            svc.Current!.X.Should().Be(834.09);
            svc.Current.Y.Should().Be(290.24);
            svc.Current.Z.Should().Be(3480.81);
            svc.Current.MeasuredAt.Offset.Should().Be(TimeSpan.Zero);
            svc.Current.Source.Should().Be(PlayerPositionSource.Movement);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task ProcessAddPlayer_spawn_envelope_populates_Current_as_Spawn_source()
    {
        // Risk 4: ProcessAddPlayer is routed to SystemSignal { PlayerAdded }
        // at L0.5, not the LocalPlayer pipe. The tracker MUST pattern-match
        // that envelope kind to recover the spawn seed.
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(SpawnEnvelope(SpawnData, seq: 1));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            svc.Current.Should().NotBeNull();
            svc.Current!.X.Should().Be(1522.22);
            svc.Current.Z.Should().Be(288.13);
            svc.Current.Source.Should().Be(PlayerPositionSource.Spawn);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Latest_position_wins()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(LocalEnvelope(PosData, seq: 1));
        driver.PushReplay(LocalEnvelope(PosData2, seq: 2,
            at: Ts.AddMinutes(25)));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            svc.Current!.X.Should().Be(-790.06);
            svc.Current.Z.Should().Be(-3386.07);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Spawn_then_movement_advances_via_both_payload_kinds()
    {
        // Real session shape: spawn (SystemSignal PlayerAdded) followed by
        // teleport / zone-change positions (LocalPlayer ProcessNewPosition).
        // The tracker's switch handles both off the unified pipe.
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(SpawnEnvelope(SpawnData, seq: 1));
        driver.PushReplay(LocalEnvelope(PosData, seq: 2));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            svc.Current!.X.Should().Be(834.09);
            svc.Current.Source.Should().Be(PlayerPositionSource.Movement);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Non_position_LocalPlayer_lines_do_not_affect_Current()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(LocalEnvelope("ProcessAddItem(Barley(1), 0, False)", seq: 1));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();
            svc.Current.Should().BeNull();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_after_observed_replays_synchronously_then_delivers_live()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(LocalEnvelope(PosData, seq: 1));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            var seen = new List<PlayerPosition>();
            using var sub = svc.Subscribe(seen.Add);
            seen.Should().ContainSingle().Which.X.Should().Be(834.09);

            driver.PushLive(LocalEnvelope(PosData2, seq: 2,
                at: Ts.AddMinutes(25)));
            await driver.DrainClassifiedAsync();

            seen.Should().HaveCount(2);
            seen[1].X.Should().Be(-790.06);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_with_no_position_does_not_invoke_handler()
    {
        using var driver = new TestLogStreamDriver();
        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);

            var seen = 0;
            using var sub = svc.Subscribe(_ => seen++);
            seen.Should().Be(0);
        }
        finally { await StopAsync(svc); }
    }

    private static async Task StartAsync(PlayerPositionTracker svc)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
    }

    private static async Task StopAsync(PlayerPositionTracker svc)
    {
        try { await svc.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* test cleanup */ }
        svc.Dispose();
    }
}
