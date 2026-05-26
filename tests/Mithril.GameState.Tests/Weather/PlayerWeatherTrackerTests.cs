using Arda.Abstractions.Logs;
using FluentAssertions;
using Mithril.GameState.Tests.TestSupport;
using Mithril.GameState.Weather;
using Xunit;

using ArdaWeatherChanged = Arda.World.Player.Events.WeatherChanged;
using ArdaAreaChanged = Arda.World.Player.Events.AreaChanged;

namespace Mithril.GameState.Tests.Weather;

/// <summary>
/// Arda Phase 3 — PlayerWeatherTracker subscribes to domain events
/// (<see cref="ArdaWeatherChanged"/> and <see cref="ArdaAreaChanged"/>)
/// via <see cref="TestDomainEventBus"/>. The zone-change race that
/// motivated #556 is structurally impossible with Arda's ordered dispatch
/// pipeline; a dedicated test pins the race-elimination property.
/// </summary>
public sealed class PlayerWeatherTrackerTests
{
    private static readonly DateTimeOffset Ts =
        new(2026, 5, 18, 19, 50, 42, TimeSpan.Zero);

    private static LogLineMetadata Meta(DateTimeOffset? ts = null) =>
        new(Timestamp: ts ?? Ts, ReadOn: ts ?? Ts, IsReplay: false);

    private static PlayerWeatherTracker NewTracker(TestDomainEventBus bus) =>
        new(bus);

    [Fact]
    public async Task First_weather_line_populates_Current_for_the_map()
    {
        var bus = new TestDomainEventBus();
        var svc = NewTracker(bus);
        try
        {
            await StartAsync(svc);

            bus.Publish(new ArdaAreaChanged(null, "AreaSerbule", Meta()));
            bus.Publish(new ArdaWeatherChanged(null, "Foggy", Meta()));

            svc.CurrentArea.Should().Be("AreaSerbule");
            svc.Current.Should().NotBeNull();
            svc.Current!.Condition.Should().Be("Foggy");
            svc.Current.MeasuredAt.Offset.Should().Be(TimeSpan.Zero);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Last_writer_wins_on_a_genuine_change()
    {
        var bus = new TestDomainEventBus();
        var svc = NewTracker(bus);
        try
        {
            await StartAsync(svc);

            bus.Publish(new ArdaAreaChanged(null, "AreaSerbule", Meta()));
            bus.Publish(new ArdaWeatherChanged(null, "Foggy", Meta()));
            bus.Publish(new ArdaWeatherChanged("Foggy", "Clear", Meta()));

            svc.Current!.Condition.Should().Be("Clear");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Map_change_drops_weather_until_the_new_map_reports()
    {
        var bus = new TestDomainEventBus();
        var svc = NewTracker(bus);
        try
        {
            await StartAsync(svc);

            bus.Publish(new ArdaAreaChanged(null, "AreaSerbule", Meta()));
            bus.Publish(new ArdaWeatherChanged(null, "Foggy", Meta()));
            svc.Current!.Condition.Should().Be("Foggy");

            var seen = new List<WeatherChanged>();
            using var sub = svc.Subscribe(seen.Add);

            bus.Publish(new ArdaAreaChanged("AreaSerbule", "AreaEltibule", Meta()));

            svc.CurrentArea.Should().Be("AreaEltibule");
            svc.Current.Should().BeNull();
            seen.Should().Contain(n =>
                n.Kind == WeatherChangeKind.AreaChanged
                && n.Area == "AreaEltibule"
                && n.State == null);

            bus.Publish(new ArdaWeatherChanged(null, "Clear", Meta()));

            svc.Current!.Condition.Should().Be("Clear");
            seen.Last().Should().Match<WeatherChanged>(n =>
                n.Kind == WeatherChangeKind.Changed
                && n.Area == "AreaEltibule"
                && n.State!.Condition == "Clear");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Idempotent_re_emit_of_same_condition_is_a_noop()
    {
        var bus = new TestDomainEventBus();
        var svc = NewTracker(bus);
        try
        {
            await StartAsync(svc);

            bus.Publish(new ArdaAreaChanged(null, "AreaSerbule", Meta()));

            var seen = new List<WeatherChanged>();
            using var sub = svc.Subscribe(seen.Add);

            bus.Publish(new ArdaWeatherChanged(null, "Foggy", Meta()));
            bus.Publish(new ArdaWeatherChanged(null, "Foggy", Meta()));
            bus.Publish(new ArdaWeatherChanged(null, "Foggy", Meta()));

            seen.Count(n => n.Kind == WeatherChangeKind.Changed).Should().Be(1);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_replays_snapshot_synchronously_then_delivers_live()
    {
        var bus = new TestDomainEventBus();
        var svc = NewTracker(bus);
        try
        {
            await StartAsync(svc);

            bus.Publish(new ArdaAreaChanged(null, "AreaSerbule", Meta()));
            bus.Publish(new ArdaWeatherChanged(null, "Foggy", Meta()));

            var seen = new List<WeatherChanged>();
            using var sub = svc.Subscribe(seen.Add);
            seen.Should().ContainSingle()
                .Which.Should().Match<WeatherChanged>(n =>
                    n.Kind == WeatherChangeKind.Snapshot && n.State!.Condition == "Foggy");

            bus.Publish(new ArdaWeatherChanged("Foggy", "Rainy", Meta()));

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
        var bus = new TestDomainEventBus();
        var svc = NewTracker(bus);
        try
        {
            await StartAsync(svc);

            bus.Publish(new ArdaAreaChanged(null, "AreaSerbule", Meta()));

            var seen = new List<WeatherChanged>();
            var sub = svc.Subscribe(seen.Add);
            bus.Publish(new ArdaWeatherChanged(null, "Foggy", Meta()));
            var countAtDispose = seen.Count;
            sub.Dispose();

            bus.Publish(new ArdaWeatherChanged("Foggy", "Rainy", Meta()));

            seen.Should().HaveCount(countAtDispose);
            svc.Current!.Condition.Should().Be("Rainy");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Weather_survives_zone_change_burst_without_stale_state_drop()
    {
        var bus = new TestDomainEventBus();
        var svc = NewTracker(bus);
        try
        {
            await StartAsync(svc);

            bus.Publish(new ArdaAreaChanged(null, "AreaSerbule", Meta()));
            bus.Publish(new ArdaWeatherChanged(null, "Foggy", Meta()));
            svc.Current!.Condition.Should().Be("Foggy");

            // The race-window burst: area change followed immediately by
            // new map's weather. On the Arda domain event bus these arrive
            // in source order — the area reconcile clears Foggy before
            // Sunny lands in the new area.
            bus.Publish(new ArdaAreaChanged("AreaSerbule", "AreaEltibule", Meta()));
            bus.Publish(new ArdaWeatherChanged(null, "Sunny", Meta()));

            svc.CurrentArea.Should().Be("AreaEltibule");
            svc.Current.Should().NotBeNull();
            svc.Current!.Condition.Should().Be("Sunny",
                because: "the domain event bus delivers AreaChanged before WeatherChanged; " +
                         "the reconcile clears Foggy first and Sunny lands in the new area");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_snapshot_stamp_is_last_event_timestamp_not_wall_clock()
    {
        var areaTs = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var weatherTs = areaTs.AddMinutes(1);

        var bus = new TestDomainEventBus();
        var svc = NewTracker(bus);
        try
        {
            await StartAsync(svc);

            bus.Publish(new ArdaAreaChanged(null, "AreaSerbule", Meta(areaTs)));
            bus.Publish(new ArdaWeatherChanged(null, "Foggy", Meta(weatherTs)));

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
                "the Snapshot stamp must come from the most-recent event the tracker applied, not wall clock");
            seenB[0].ObservedAt.Should().Be(seenA[0].ObservedAt,
                "late subscribers observing the same tracker state must see identical snapshot timestamps");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Area_change_notification_uses_event_timestamp_not_wall_clock()
    {
        var firstAreaTs = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var secondAreaTs = firstAreaTs.AddMinutes(7);

        var bus = new TestDomainEventBus();
        var svc = NewTracker(bus);
        try
        {
            await StartAsync(svc);

            var seen = new List<WeatherChanged>();
            using var sub = svc.Subscribe(seen.Add);

            bus.Publish(new ArdaAreaChanged(null, "AreaSerbule", Meta(firstAreaTs)));

            await Task.Delay(TimeSpan.FromMilliseconds(50));

            bus.Publish(new ArdaAreaChanged("AreaSerbule", "AreaEltibule", Meta(secondAreaTs)));

            var areaChanges = seen.Where(n => n.Kind == WeatherChangeKind.AreaChanged).ToList();
            areaChanges.Should().HaveCount(2,
                "one AreaChanged per event (initial + the second area change)");
            areaChanges[0].ObservedAt.Should().Be(firstAreaTs,
                "ReconcileArea must stamp the AreaChanged note with the triggering event's timestamp, not wall clock");
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
