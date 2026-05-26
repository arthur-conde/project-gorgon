using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.GameState.Celestial;
using Mithril.Shared.Diagnostics;
using Xunit;

namespace Mithril.GameState.Tests.Celestial;

public sealed class PlayerCelestialStateServiceTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 5, 18, 19, 50, 42, TimeSpan.Zero);

    private static LogLineMetadata Meta(DateTimeOffset ts, bool isReplay = false) =>
        new(ts, ts, isReplay);

    private static CelestialInfoChanged Evt(string rawPhase, DateTimeOffset ts, string? previous = null)
    {
        var phase = Arda.World.Player.MoonPhaseExtensions.ParsePhase(rawPhase);
        var displayName = Arda.World.Player.MoonPhaseExtensions.DisplayName(phase, rawPhase);
        return new(previous, rawPhase, phase, displayName, Meta(ts));
    }

    private static PlayerCelestialStateService NewService(
        TestDomainBus bus, IDiagnosticsSink? diag = null) =>
        new(bus, diag);

    [Fact]
    public void Cold_start_Current_is_null()
    {
        var bus = new TestDomainBus();
        using var svc = NewService(bus);
        svc.Current.Should().BeNull();
    }

    [Fact]
    public void Celestial_event_populates_Current_with_phase_and_utc_instant()
    {
        var bus = new TestDomainBus();
        using var svc = NewService(bus);

        bus.Publish(Evt("WaxingCrescentMoon", Stamp));

        svc.Current.Should().NotBeNull();
        svc.Current!.Phase.Should().Be(MoonPhase.WaxingCrescent);
        svc.Current.RawPhase.Should().Be("WaxingCrescentMoon");
        svc.Current.DisplayName.Should().Be("Waxing Crescent");
        svc.Current.MeasuredAt.Should().Be(Stamp);
        svc.Current.MeasuredAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Latest_phase_wins_on_rollover()
    {
        var bus = new TestDomainBus();
        using var svc = NewService(bus);

        bus.Publish(Evt("WaxingCrescentMoon", Stamp));
        bus.Publish(Evt("FullMoon", Stamp.AddMinutes(40), "WaxingCrescentMoon"));

        svc.Current!.Phase.Should().Be(MoonPhase.FullMoon);
        svc.Current.RawPhase.Should().Be("FullMoon");
    }

    [Fact]
    public void Subscribe_after_observed_replays_synchronously_then_delivers_live()
    {
        var bus = new TestDomainBus();
        using var svc = NewService(bus);

        bus.Publish(Evt("WaxingCrescentMoon", Stamp));

        var seen = new List<CelestialInfo>();
        using var sub = svc.Subscribe(seen.Add);
        seen.Should().ContainSingle().Which.Phase.Should().Be(MoonPhase.WaxingCrescent);

        bus.Publish(Evt("FullMoon", Stamp.AddMinutes(40), "WaxingCrescentMoon"));

        seen.Should().HaveCount(2);
        seen[1].Phase.Should().Be(MoonPhase.FullMoon);
    }

    [Fact]
    public void Subscribe_with_no_phase_does_not_invoke_handler()
    {
        var bus = new TestDomainBus();
        using var svc = NewService(bus);

        var seen = 0;
        using var sub = svc.Subscribe(_ => seen++);
        seen.Should().Be(0);
    }

    [Fact]
    public void Disposed_subscription_stops_receiving()
    {
        var bus = new TestDomainBus();
        using var svc = NewService(bus);

        var seen = new List<CelestialInfo>();
        var sub = svc.Subscribe(seen.Add);
        sub.Dispose();

        bus.Publish(Evt("WaxingCrescentMoon", Stamp));

        seen.Should().BeEmpty();
        svc.Current.Should().NotBeNull("the service still tracks state even with no subscribers");
    }

    [Fact]
    public void Unmapped_token_writes_an_Error_diagnostic_once_per_distinct_token()
    {
        var bus = new TestDomainBus();
        var diag = new DiagnosticsSink();
        using var svc = NewService(bus, diag);

        bus.Publish(Evt("BloodMoonEclipse", Stamp));
        bus.Publish(Evt("BloodMoonEclipse", Stamp.AddMinutes(40)));
        bus.Publish(Evt("BloodMoonEclipse", Stamp.AddHours(1)));
        bus.Publish(Evt("VoidMoon", Stamp.AddHours(2), "BloodMoonEclipse"));

        var errors = diag.Snapshot()
            .Where(e => e.Level == DiagnosticLevel.Error
                        && e.Category == "GameState.Celestial")
            .ToArray();

        errors.Should().HaveCount(2, "one Error per distinct unmapped token, deduped on replay");
        errors.Should().ContainSingle(e => e.Message.Contains("'BloodMoonEclipse'"));
        errors.Should().ContainSingle(e => e.Message.Contains("'VoidMoon'"));
        errors[0].Message.Should().Contain("no MoonPhase enum member");

        svc.Current!.Phase.Should().Be(MoonPhase.Unknown);
        svc.Current.RawPhase.Should().Be("VoidMoon");
    }

    [Fact]
    public void Recognised_tokens_emit_no_Error_diagnostic()
    {
        var bus = new TestDomainBus();
        var diag = new DiagnosticsSink();
        using var svc = NewService(bus, diag);

        bus.Publish(Evt("WaxingCrescentMoon", Stamp));
        bus.Publish(Evt("FullMoon", Stamp.AddMinutes(40), "WaxingCrescentMoon"));

        diag.Snapshot().Should().NotContain(e => e.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void Replay_idempotence_same_events_yield_identical_state()
    {
        var bus1 = new TestDomainBus();
        using var svc1 = NewService(bus1);
        bus1.Publish(Evt("WaxingCrescentMoon", Stamp));
        bus1.Publish(Evt("FullMoon", Stamp.AddMinutes(40), "WaxingCrescentMoon"));
        var firstPhase = svc1.Current!.Phase;
        var firstRaw = svc1.Current.RawPhase;

        var bus2 = new TestDomainBus();
        using var svc2 = NewService(bus2);
        bus2.Publish(Evt("WaxingCrescentMoon", Stamp) with { Metadata = Meta(Stamp, isReplay: true) });
        bus2.Publish(Evt("FullMoon", Stamp.AddMinutes(40), "WaxingCrescentMoon"));

        svc2.Current.Should().NotBeNull();
        svc2.Current!.Phase.Should().Be(firstPhase);
        svc2.Current.RawPhase.Should().Be(firstRaw);
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
