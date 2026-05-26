using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class EffectsTests
{
    private readonly SpyEventBus _bus = new();
    private readonly Effects _effects;

    public EffectsTests()
    {
        _effects = new Effects(_bus);
    }

    private static LogLineMetadata Meta(bool isReplay = false) =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, isReplay);

    [Fact]
    public void AddEffects_EmitsCatalogIds()
    {
        var args = "(12345, 67890, \"[302, 303]\", True)".AsSpan();
        _effects.OnAdd(args, "source", Meta());

        _bus.Published<EffectsAdded>().Should().ContainSingle()
            .Which.CatalogIds.Should().BeEquivalentTo([302, 303]);
    }

    [Fact]
    public void AddEffects_TrailingComma_Tolerated()
    {
        var args = "(12345, 67890, \"[15361, ]\", False)".AsSpan();
        _effects.OnAdd(args, "source", Meta());

        _bus.Published<EffectsAdded>().Should().ContainSingle()
            .Which.CatalogIds.Should().BeEquivalentTo([15361]);
    }

    [Fact]
    public void RemoveEffects_EmitsInstanceIds()
    {
        var args = "(12345, [259278, 259279])".AsSpan();
        _effects.OnRemove(args, "source", Meta());

        _bus.Published<EffectsRemoved>().Should().ContainSingle()
            .Which.InstanceIds.Should().BeEquivalentTo([259278L, 259279L]);
    }

    [Fact]
    public void AddEffects_EmptyList_NoEvent()
    {
        var args = "(12345, 67890, \"[]\", True)".AsSpan();
        _effects.OnAdd(args, "source", Meta());

        _bus.Published<EffectsAdded>().Should().BeEmpty();
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
