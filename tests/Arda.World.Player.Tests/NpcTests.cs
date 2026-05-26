using System.Collections.Frozen;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class NpcTests
{
    private readonly SpyEventBus _bus = new();
    private readonly Npc _npc;

    public NpcTests()
    {
        var pool = new InternPool(FrozenDictionary<string, string>.Empty);
        _npc = new Npc(_bus, pool);
    }

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    private void DispatchInteraction(string interactionArgs)
    {
        var line = $"LocalPlayer: ProcessStartInteraction({interactionArgs})";
        _npc.OnStartInteraction($"({interactionArgs})".AsSpan(), line, Meta());
    }

    private void DispatchDeleteItem(long instanceId)
    {
        var line = $"LocalPlayer: ProcessDeleteItem({instanceId})";
        _npc.OnDeleteItem($"({instanceId})".AsSpan(), line, Meta());
    }

    // ── ProcessStartInteraction — NPC targets ───────────────────────────

    [Fact]
    public void NpcInteraction_SetsActiveContext()
    {
        DispatchInteraction("12307, 7, 2405.813, True, \"NPC_Joe\"");

        _npc.ActiveNpcKey.Should().Be("NPC_Joe");
        _npc.ActiveEntityId.Should().Be(12307);
        _npc.ActiveFavor.Should().BeApproximately(2405.813, 0.001);
    }

    [Fact]
    public void NpcInteraction_EmitsEvent()
    {
        DispatchInteraction("12307, 7, 2405.813, True, \"NPC_Joe\"");

        var evt = _bus.Published<InteractionStarted>().Should().ContainSingle().Which;
        evt.EntityId.Should().Be(12307);
        evt.Name.Should().Be("NPC_Joe");
        evt.Favor.Should().BeApproximately(2405.813, 0.001);
        evt.IsNpc.Should().BeTrue();
    }

    [Fact]
    public void NpcInteraction_WithZeroFavor()
    {
        DispatchInteraction("8887, 7, 0, True, \"NPC_Way\"");

        _npc.ActiveNpcKey.Should().Be("NPC_Way");
        _npc.ActiveFavor.Should().Be(0);
    }

    // ── ProcessStartInteraction — non-NPC targets ───────────────────────

    [Fact]
    public void NonNpcInteraction_ClearsContext()
    {
        DispatchInteraction("12307, 7, 2405.813, True, \"NPC_Joe\"");
        _npc.ActiveNpcKey.Should().Be("NPC_Joe");

        DispatchInteraction("25237464, 7, 0, False, \"ShallowGrave1\"");

        _npc.ActiveNpcKey.Should().BeNull();
        _npc.ActiveEntityId.Should().BeNull();
    }

    [Fact]
    public void NonNpcInteraction_StillEmitsEvent()
    {
        DispatchInteraction("25237464, 7, 0, False, \"ShallowGrave1\"");

        var evt = _bus.Published<InteractionStarted>().Should().ContainSingle().Which;
        evt.EntityId.Should().Be(25237464);
        evt.Name.Should().Be("ShallowGrave1");
        evt.IsNpc.Should().BeFalse();
    }

    [Fact]
    public void EmptyNameInteraction_ClearsContext()
    {
        DispatchInteraction("12307, 7, 2405.813, True, \"NPC_Joe\"");
        DispatchInteraction("25286843, 11.5, 0, False, \"\"");

        _npc.ActiveNpcKey.Should().BeNull();
    }

    // ── Gift pending state (delete without favor) ────────────────────────

    [Fact]
    public void DeleteItemDuringNpcInteraction_SetsPending_NoImmediateEvent()
    {
        DispatchInteraction("12307, 7, 2405.813, True, \"NPC_Joe\"");
        _bus.Clear();

        DispatchDeleteItem(84741837);

        _bus.Published<GiftAccepted>().Should().BeEmpty(
            "a delete alone sets pending state; GiftAccepted requires a correlated DeltaFavor");
    }

    [Fact]
    public void DeleteItemWithoutActiveInteraction_NoEvent()
    {
        DispatchDeleteItem(84741837);

        _bus.Published<GiftAccepted>().Should().BeEmpty();
    }

    [Fact]
    public void DeleteItemAfterNonNpcInteraction_NoEvent()
    {
        DispatchInteraction("25237464, 7, 0, False, \"ShallowGrave1\"");
        _bus.Clear();

        DispatchDeleteItem(84741837);

        _bus.Published<GiftAccepted>().Should().BeEmpty();
    }

    // ── Interning ───────────────────────────────────────────────────────

    [Fact]
    public void NpcKey_IsInterned_AcrossRepeatedInteractions()
    {
        DispatchInteraction("12307, 7, 100, True, \"NPC_Joe\"");
        var first = _npc.ActiveNpcKey;

        DispatchInteraction("25237464, 7, 0, False, \"ShallowGrave1\"");
        DispatchInteraction("12307, 7, 200, True, \"NPC_Joe\"");
        var second = _npc.ActiveNpcKey;

        ReferenceEquals(first, second).Should().BeTrue(
            "InternPool should return the same string instance for repeated NPC keys");
    }

    // ── SpyEventBus ─────────────────────────────────────────────────────

    private sealed class SpyEventBus : IDomainEventBus
    {
        private readonly Dictionary<Type, List<object>> _published = [];

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
            => new NoopDisposable();

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

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
