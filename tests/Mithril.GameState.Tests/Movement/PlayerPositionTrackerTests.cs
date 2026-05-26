using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.GameState.Movement;
using Xunit;

namespace Mithril.GameState.Tests.Movement;

/// <summary>
/// Arda Phase 3 — PlayerPositionTracker subscribes to
/// <see cref="PlayerPositionChanged"/> domain events via
/// <see cref="IDomainEventSubscriber"/>. Tests drive the bus directly.
/// </summary>
public sealed class PlayerPositionTrackerTests
{
    private static readonly DateTimeOffset Ts =
        new(2026, 5, 18, 10, 45, 47, TimeSpan.Zero);

    private static LogLineMetadata Meta(DateTimeOffset? ts = null) =>
        new(Timestamp: ts ?? Ts, ReadOn: DateTimeOffset.UtcNow, IsReplay: false);

    private static PlayerPositionChanged MovementEvent(
        double x, double y, double z, DateTimeOffset? ts = null) =>
        new(x, y, z, PositionSource.Movement, Meta(ts));

    private static PlayerPositionChanged SpawnEvent(
        double x, double y, double z, DateTimeOffset? ts = null) =>
        new(x, y, z, PositionSource.Spawn, Meta(ts));

    [Fact]
    public async Task Position_event_populates_Current_with_coords_and_utc_instant()
    {
        var bus = new TestDomainBus();
        var svc = new PlayerPositionTracker(bus);
        try
        {
            await StartAsync(svc);
            bus.Publish(MovementEvent(834.09, 290.24, 3480.81));

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
    public async Task Spawn_event_populates_Current_as_Spawn_source()
    {
        var bus = new TestDomainBus();
        var svc = new PlayerPositionTracker(bus);
        try
        {
            await StartAsync(svc);
            bus.Publish(SpawnEvent(1522.22, 112.27, 288.13));

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
        var bus = new TestDomainBus();
        var svc = new PlayerPositionTracker(bus);
        try
        {
            await StartAsync(svc);
            bus.Publish(MovementEvent(834.09, 290.24, 3480.81));
            bus.Publish(MovementEvent(-790.06, 309.18, -3386.07, Ts.AddMinutes(25)));

            svc.Current!.X.Should().Be(-790.06);
            svc.Current.Z.Should().Be(-3386.07);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Spawn_then_movement_advances_via_both_source_kinds()
    {
        var bus = new TestDomainBus();
        var svc = new PlayerPositionTracker(bus);
        try
        {
            await StartAsync(svc);
            bus.Publish(SpawnEvent(1522.22, 112.27, 288.13));
            bus.Publish(MovementEvent(834.09, 290.24, 3480.81));

            svc.Current!.X.Should().Be(834.09);
            svc.Current.Source.Should().Be(PlayerPositionSource.Movement);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_after_observed_replays_synchronously_then_delivers_live()
    {
        var bus = new TestDomainBus();
        var svc = new PlayerPositionTracker(bus);
        try
        {
            await StartAsync(svc);
            bus.Publish(MovementEvent(834.09, 290.24, 3480.81));

            var seen = new List<PlayerPosition>();
            using var sub = svc.Subscribe(seen.Add);
            seen.Should().ContainSingle().Which.X.Should().Be(834.09);

            bus.Publish(MovementEvent(-790.06, 309.18, -3386.07, Ts.AddMinutes(25)));

            seen.Should().HaveCount(2);
            seen[1].X.Should().Be(-790.06);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_with_no_position_does_not_invoke_handler()
    {
        var bus = new TestDomainBus();
        var svc = new PlayerPositionTracker(bus);
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

    private sealed class TestDomainBus : IDomainEventSubscriber
    {
        private readonly object _lock = new();
        private readonly Dictionary<Type, List<object>> _handlers = new();

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list))
                    _handlers[typeof(T)] = list = new List<object>();
                list.Add(handler);
                return new Sub(this, typeof(T), handler);
            }
        }

        public void Publish<T>(T evt) where T : struct
        {
            List<object>? snap;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(typeof(T), out var list)) return;
                snap = list.ToList();
            }
            foreach (var h in snap) ((Action<T>)h)(evt);
        }

        private sealed class Sub(TestDomainBus o, Type t, object h) : IDisposable
        {
            public void Dispose()
            {
                lock (o._lock) { if (o._handlers.TryGetValue(t, out var list)) list.Remove(h); }
            }
        }
    }
}
