using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class MapPinTests
{
    private readonly SpyEventBus _bus = new();
    private readonly MapPins _mapPins;

    public MapPinTests()
    {
        _mapPins = new MapPins(_bus);
    }

    private static LogLineMetadata Meta(bool isReplay = false) =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, isReplay);

    private void DispatchAdd(int a, int shape, int color, double x, double y, double z, string label)
    {
        var args = $"({a}, {shape}, {color}, ({x}, {y}, {z}), \"{label}\")";
        _mapPins.PinAddHandler.Handle(args.AsSpan(), "source", Meta());
    }

    private void DispatchRemove(int a, int shape, int color, double x, double y, double z, string label)
    {
        var args = $"({a}, {shape}, {color}, ({x}, {y}, {z}), \"{label}\")";
        _mapPins.PinRemoveHandler.Handle(args.AsSpan(), "source", Meta());
    }

    [Fact]
    public void AddPin_EmitsEventAndTracksState()
    {
        DispatchAdd(1, 0, 1, 1425.06, 0.00, 2924.99, "South");

        _mapPins.Pins.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                X = 1425.06,
                Z = 2924.99,
                Label = "South",
                Shape = 0,
                Color = 1
            });

        _bus.Published<MapPinAdded>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                X = 1425.06,
                Z = 2924.99,
                Label = "South",
                Shape = 0,
                Color = 1
            });
    }

    [Fact]
    public void RemovePin_RemovesFromStateAndEmitsEvent()
    {
        DispatchAdd(1, 0, 0, 784.74, 0.00, 3429.94, "Camp");
        _bus.Clear();

        DispatchRemove(1, 0, 0, 784.74, 0.00, 3429.94, "");

        _mapPins.Pins.Should().BeEmpty();
        _bus.Published<MapPinRemoved>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                X = 784.74,
                Z = 3429.94,
                Label = ""
            });
    }

    [Fact]
    public void MultiplePins_TrackedIndependently()
    {
        DispatchAdd(1, 0, 1, 100.0, 0.0, 200.0, "A");
        DispatchAdd(1, 1, 2, 300.0, 0.0, 400.0, "B");

        _mapPins.Pins.Should().HaveCount(2);
    }

    [Fact]
    public void Reset_ClearsAllPins()
    {
        DispatchAdd(1, 0, 0, 100.0, 0.0, 200.0, "A");
        DispatchAdd(1, 0, 0, 300.0, 0.0, 400.0, "B");

        _mapPins.Reset();

        _mapPins.Pins.Should().BeEmpty();
    }

    [Fact]
    public void NegativeCoordinates_ParseCorrectly()
    {
        DispatchAdd(1, 0, 0, -500.5, 0.0, -1200.3, "Far");

        _mapPins.Pins.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { X = -500.5, Z = -1200.3 });
    }

    [Fact]
    public void EmptyLabel_ParsedAsEmptyString()
    {
        DispatchAdd(1, 0, 0, 100.0, 0.0, 200.0, "");

        _mapPins.Pins.Should().ContainSingle()
            .Which.Label.Should().BeEmpty();
    }

    private sealed class SpyEventBus : IDomainEventBus
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
