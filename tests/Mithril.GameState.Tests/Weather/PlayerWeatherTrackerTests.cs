using FluentAssertions;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Tests.TestSupport;
using Mithril.GameState.Weather;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Weather;

/// <summary>
/// L0.5 #556 Phase 3 — PlayerWeatherTracker now subscribes to the L1
/// driver's unified classified pipe; tests drive it via
/// <see cref="TestLogStreamDriver"/>. The zone-change race that motivated
/// #556 (Weather seeing a new condition before the area pump advanced)
/// is structurally impossible with the single-subscription unified pipe;
/// a dedicated test pins the race-elimination property.
/// </summary>
public sealed class PlayerWeatherTrackerTests
{
    private static readonly DateTimeOffset Ts =
        new(2026, 5, 18, 19, 50, 42, TimeSpan.Zero);

    private static PlayerWeatherTracker NewTracker(TestLogStreamDriver driver) =>
        new(driver, new WeatherLogParser(), new AreaTransitionParser());

    private static SystemSignalLogLine AreaEnvelope(string areaKey, long seq) =>
        new(
            Timestamp: Ts,
            Kind: SystemSignalKind.AreaLoading,
            Data: $"LOADING LEVEL {areaKey}",
            Sequence: seq,
            ReadMonotonicTicks: 0);

    private static LocalPlayerLogLine WeatherEnvelope(string condition, bool flag, long seq) =>
        new(
            Timestamp: Ts,
            Data: $"ProcessSetWeather(\"{condition}\", {(flag ? "True" : "False")})",
            Sequence: seq,
            ReadMonotonicTicks: 0);

    [Fact]
    public async Task First_weather_line_populates_Current_for_the_map()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));
        driver.PushReplay(WeatherEnvelope("Foggy", flag: true, seq: 2));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            svc.CurrentArea.Should().Be("AreaSerbule");
            svc.Current.Should().NotBeNull();
            svc.Current!.Condition.Should().Be("Foggy");
            svc.Current.Flag.Should().BeTrue();
            svc.Current.MeasuredAt.Offset.Should().Be(TimeSpan.Zero);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Last_writer_wins_on_a_genuine_change()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));
        driver.PushReplay(WeatherEnvelope("Foggy", flag: true, seq: 2));
        driver.PushReplay(WeatherEnvelope("Clear", flag: false, seq: 3));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            svc.Current!.Condition.Should().Be("Clear");
            svc.Current.Flag.Should().BeFalse();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Map_change_drops_weather_until_the_new_map_reports()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));
        driver.PushReplay(WeatherEnvelope("Foggy", flag: true, seq: 2));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();
            svc.Current!.Condition.Should().Be("Foggy");

            var seen = new List<WeatherChanged>();
            using var sub = svc.Subscribe(seen.Add); // Snapshot replay first

            // Leave the foggy map: weather must not bleed into the next map.
            driver.PushLive(AreaEnvelope("AreaEltibule", seq: 3));
            await driver.DrainClassifiedAsync();

            svc.CurrentArea.Should().Be("AreaEltibule");
            svc.Current.Should().BeNull(); // unknown — NOT still Foggy
            seen.Should().Contain(n =>
                n.Kind == WeatherChangeKind.AreaChanged
                && n.Area == "AreaEltibule"
                && n.State == null);

            // New map reports its own weather.
            driver.PushLive(WeatherEnvelope("Clear", flag: false, seq: 4));
            await driver.DrainClassifiedAsync();

            svc.Current!.Condition.Should().Be("Clear");
            seen.Last().Should().Match<WeatherChanged>(n =>
                n.Kind == WeatherChangeKind.Changed
                && n.Area == "AreaEltibule"
                && n.State!.Condition == "Clear");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Replay_of_unchanged_weather_is_idempotent_no_extra_events()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));
        driver.PushReplay(WeatherEnvelope("Foggy", flag: true, seq: 2));
        // Zone re-emits the current weather verbatim.
        driver.PushReplay(WeatherEnvelope("Foggy", flag: true, seq: 3));
        driver.PushReplay(WeatherEnvelope("Foggy", flag: true, seq: 4));

        var svc = NewTracker(driver);
        try
        {
            var seen = new List<WeatherChanged>();
            using var sub = svc.Subscribe(seen.Add); // Snapshot first

            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            // 1 Snapshot (from Subscribe before Start) + 1 Changed (first
            // Foggy line) + 0 from idempotent re-emits = 2 notifications.
            // The Snapshot has State == null because Subscribe ran before
            // the tracker had observed anything; the Changed brings it to
            // Foggy and the re-emits are silent.
            seen.Count(n => n.Kind == WeatherChangeKind.Changed).Should().Be(1);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_replays_snapshot_synchronously_then_delivers_live()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));
        driver.PushReplay(WeatherEnvelope("Foggy", flag: true, seq: 2));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            var seen = new List<WeatherChanged>();
            using var sub = svc.Subscribe(seen.Add);
            seen.Should().ContainSingle()
                .Which.Should().Match<WeatherChanged>(n =>
                    n.Kind == WeatherChangeKind.Snapshot && n.State!.Condition == "Foggy");

            driver.PushLive(WeatherEnvelope("Rainy", flag: true, seq: 3));
            await driver.DrainClassifiedAsync();

            var last = seen.Last();
            last.Kind.Should().Be(WeatherChangeKind.Changed);
            last.State!.Condition.Should().Be("Rainy");
            last.ObservedAt.Offset.Should().Be(TimeSpan.Zero);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Disposed_subscription_stops_receiving_but_state_advances()
    {
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            var seen = new List<WeatherChanged>();
            var sub = svc.Subscribe(seen.Add);
            driver.PushLive(WeatherEnvelope("Foggy", flag: true, seq: 2));
            await driver.DrainClassifiedAsync();
            var countAtDispose = seen.Count;
            sub.Dispose();

            driver.PushLive(WeatherEnvelope("Rainy", flag: true, seq: 3));
            await driver.DrainClassifiedAsync();

            seen.Should().HaveCount(countAtDispose); // no further delivery
            svc.Current!.Condition.Should().Be("Rainy"); // state still advances
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Weather_survives_zone_change_burst_without_stale_state_drop()
    {
        // #556 race-elimination targeted test. Pre-#556 a zone-change burst
        //   LOADING LEVEL AreaNewMap
        //   ProcessSetWeather Sunny
        // could be processed by two pumps in the wrong order — Weather
        // would see Sunny first against the OLD area's tracked state,
        // then the area pump would advance and Reconcile would DROP Sunny
        // as if it had been a "stale prior-area" emission. On the unified
        // pipe the two envelopes arrive on one subscription in source
        // order, so the reconcile lands before the weather-set and Sunny
        // is recorded under the new area.
        using var driver = new TestLogStreamDriver();
        driver.PushReplay(AreaEnvelope("AreaSerbule", seq: 1));
        driver.PushReplay(WeatherEnvelope("Foggy", flag: true, seq: 2));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();
            svc.Current!.Condition.Should().Be("Foggy");

            // The race-window burst:
            driver.PushLive(AreaEnvelope("AreaEltibule", seq: 3));
            driver.PushLive(WeatherEnvelope("Sunny", flag: true, seq: 4));
            await driver.DrainClassifiedAsync();

            svc.CurrentArea.Should().Be("AreaEltibule");
            svc.Current.Should().NotBeNull();
            svc.Current!.Condition.Should().Be("Sunny",
                because: "the unified pipe delivers LOADING LEVEL before ProcessSetWeather; " +
                         "the reconcile clears Foggy first and Sunny lands in the new area");
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
        // subscribers regardless of when they attach. Mirrors the
        // PlayerPinTracker test.
        var areaTs = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var weatherTs = areaTs.AddMinutes(1);

        using var driver = new TestLogStreamDriver();
        driver.PushReplay(new SystemSignalLogLine(
            Timestamp: areaTs,
            Kind: SystemSignalKind.AreaLoading,
            Data: "LOADING LEVEL AreaSerbule",
            Sequence: 1,
            ReadMonotonicTicks: 0));
        driver.PushReplay(new LocalPlayerLogLine(
            Timestamp: weatherTs,
            Data: "ProcessSetWeather(\"Foggy\", True)",
            Sequence: 2,
            ReadMonotonicTicks: 0));

        var svc = NewTracker(driver);
        try
        {
            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            await Task.Delay(TimeSpan.FromMilliseconds(50));

            var seenA = new List<WeatherChanged>();
            using var subA = svc.Subscribe(seenA.Add);

            await Task.Delay(TimeSpan.FromMilliseconds(50));

            var seenB = new List<WeatherChanged>();
            using var subB = svc.Subscribe(seenB.Add);

            seenA.Should().ContainSingle()
                .Which.Kind.Should().Be(WeatherChangeKind.Snapshot);
            seenB.Should().ContainSingle()
                .Which.Kind.Should().Be(WeatherChangeKind.Snapshot);

            seenA[0].ObservedAt.Should().Be(weatherTs,
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
        // Mirrors the PlayerPinTracker test.
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
            var seen = new List<WeatherChanged>();
            using var sub = svc.Subscribe(seen.Add);

            await StartAsync(svc);
            await driver.DrainClassifiedAsync();

            await Task.Delay(TimeSpan.FromMilliseconds(50));

            driver.PushLive(new SystemSignalLogLine(
                Timestamp: secondAreaTs,
                Kind: SystemSignalKind.AreaLoading,
                Data: "LOADING LEVEL AreaEltibule",
                Sequence: 2,
                ReadMonotonicTicks: 0));
            await driver.DrainClassifiedAsync();

            var areaChanges = seen.Where(n => n.Kind == WeatherChangeKind.AreaChanged).ToList();
            areaChanges.Should().HaveCount(2,
                "one AreaChanged per envelope (initial replay + the live push)");
            areaChanges[0].ObservedAt.Should().Be(firstAreaTs,
                "ReconcileArea must stamp the AreaChanged note with the triggering envelope's timestamp, not wall clock");
            areaChanges[1].ObservedAt.Should().Be(secondAreaTs);
        }
        finally { await StopAsync(svc); }
    }

    private static async Task StartAsync(PlayerWeatherTracker svc)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
    }

    private static async Task StopAsync(PlayerWeatherTracker svc)
    {
        try { await svc.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* test cleanup */ }
        svc.Dispose();
    }
}
