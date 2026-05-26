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
        var args = "(12345, 67890, \"[302, 303]\", True)";
        _effects.AddHandler.Handle(args.AsSpan(), "source", Meta());

        var published = _bus.Published<EffectsAdded>();
        published.Should().ContainSingle();
        published[0].CatalogIds.Should().BeEquivalentTo([302, 303]);
        published[0].SourceCharId.Should().Be(67890);
    }

    [Fact]
    public void AddEffects_TrailingComma_Tolerated()
    {
        var args = "(12345, 67890, \"[15361, ]\", False)";
        _effects.AddHandler.Handle(args.AsSpan(), "source", Meta());

        _bus.Published<EffectsAdded>().Should().ContainSingle()
            .Which.CatalogIds.Should().BeEquivalentTo([15361]);
    }

    [Fact]
    public void RemoveEffects_EmitsInstanceIds()
    {
        var args = "(12345, [259278, 259279])";
        _effects.RemoveHandler.Handle(args.AsSpan(), "source", Meta());

        _bus.Published<EffectsRemoved>().Should().ContainSingle()
            .Which.InstanceIds.Should().BeEquivalentTo([259278L, 259279L]);
    }

    [Fact]
    public void AddEffects_EmptyList_NoEvent()
    {
        var args = "(12345, 67890, \"[]\", True)";
        _effects.AddHandler.Handle(args.AsSpan(), "source", Meta());

        _bus.Published<EffectsAdded>().Should().BeEmpty();
    }

    [Fact]
    public void UpdateEffectName_EmitsInstanceIdAndDisplayName()
    {
        var args = "(25098977, 259320, \"Performance Appreciation, Level 0\")";
        _effects.UpdateNameHandler.Handle(args.AsSpan(), "source", Meta());

        var published = _bus.Published<EffectNameUpdated>();
        published.Should().ContainSingle();
        published[0].InstanceId.Should().Be(259320);
        published[0].DisplayName.Should().Be("Performance Appreciation, Level 0");
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
