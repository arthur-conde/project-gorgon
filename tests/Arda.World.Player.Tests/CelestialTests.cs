using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class CelestialTests
{
    private readonly SpyEventBus _bus = new();
    private readonly Celestial _celestial;

    public CelestialTests()
    {
        _celestial = new Celestial(_bus);
    }

    private static LogLineMetadata Meta(bool isReplay = false) =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, isReplay);

    private void Dispatch(string phase)
    {
        var args = $"({phase})".AsSpan();
        _celestial.Handle(args, default, $"LocalPlayer: ProcessSetCelestialInfo({phase})", Meta());
    }

    [Fact]
    public void FirstPhase_EmitsEvent()
    {
        Dispatch("WaxingCrescentMoon");

        _celestial.CurrentPhaseRaw.Should().Be("WaxingCrescentMoon");
        _bus.Published<CelestialInfoChanged>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                PreviousRawPhase = (string?)null,
                RawPhase = "WaxingCrescentMoon"
            });
    }

    [Fact]
    public void SamePhase_DoesNotEmit()
    {
        Dispatch("FullMoon");
        _bus.Clear();

        Dispatch("FullMoon");

        _bus.Published<CelestialInfoChanged>().Should().BeEmpty();
    }

    [Fact]
    public void PhaseTransition_ReportsPrevious()
    {
        Dispatch("NewMoon");
        _bus.Clear();

        Dispatch("WaxingCrescentMoon");

        _celestial.CurrentPhaseRaw.Should().Be("WaxingCrescentMoon");
        _bus.Published<CelestialInfoChanged>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                PreviousRawPhase = "NewMoon",
                RawPhase = "WaxingCrescentMoon"
            });
    }

    [Fact]
    public void Reset_ClearsState()
    {
        Dispatch("FullMoon");
        _celestial.Reset();

        _celestial.CurrentPhaseRaw.Should().BeNull();
    }

    private sealed class SpyEventBus : IDomainEventSubscriber, IDomainEventPublisher
    {
        private readonly Dictionary<Type, List<object>> _published = [];

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct => new NoopDisposable();

        public void Publish<T>(T domainEvent) where T : struct
        {
            if (!_published.TryGetValue(typeof(T), out var list))
            {
                list = [];
                _published[typeof(T)] = list;
            }
            list.Add(domainEvent);
        }

        public List<T> Published<T>() where T : struct
        {
            if (_published.TryGetValue(typeof(T), out var list))
                return list.Cast<T>().ToList();
            return [];
        }

        public void Clear() => _published.Clear();

        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
