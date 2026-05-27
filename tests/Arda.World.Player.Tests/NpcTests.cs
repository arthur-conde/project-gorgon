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
        _npc.OnStartInteraction($"({interactionArgs})".AsSpan(), default, line, Meta());
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

    // ── ProcessEndInteraction clears pending state (regression: phantom gifts) ──

    private void DispatchEndInteraction(long entityId)
    {
        var args = $"({entityId})";
        _npc.OnEndInteraction(args.AsSpan(), $"LocalPlayer: ProcessEndInteraction{args}", Meta());
    }

    private void DispatchDeltaFavor(string deltaFavorArgs)
    {
        var line = $"LocalPlayer: ProcessDeltaFavor({deltaFavorArgs})";
        _npc.OnDeltaFavor($"({deltaFavorArgs})".AsSpan(), default, line, Meta());
    }

    [Fact]
    public void EndInteraction_ClearsActiveContextAndPendingDelete()
    {
        DispatchInteraction("12307, 7, 2405.813, True, \"NPC_Joe\"");
        DispatchDeleteItem(84741837); // stash pending
        DispatchEndInteraction(12307);

        _npc.ActiveNpcKey.Should().BeNull();

        // A later interaction with a different NPC + positive delta must NOT
        // resurrect the prior pending delete.
        DispatchInteraction("99999, 7, 100, True, \"NPC_Other\"");
        _bus.Clear();
        DispatchDeltaFavor("99999, \"NPC_Other\", 25, True");

        _bus.Published<GiftAccepted>().Should().BeEmpty(
            "ProcessEndInteraction cleared the stashed delete; the new delta must not fabricate a gift");
    }

    [Fact]
    public void NegativeDeltaInSameInteraction_ClearsPendingDelete()
    {
        DispatchInteraction("12307, 7, 2405.813, True, \"NPC_Joe\"");
        DispatchDeleteItem(84741837); // stash pending

        // Failed gift (delta <= 0) inside the same interaction
        DispatchDeltaFavor("12307, \"NPC_Joe\", 0, True");

        _bus.Clear();
        // A later positive delta in the SAME interaction must NOT pair with
        // the cleared pending delete.
        DispatchDeltaFavor("12307, \"NPC_Joe\", 25, True");

        _bus.Published<GiftAccepted>().Should().BeEmpty(
            "non-positive delta clears the stashed delete; later positive delta has nothing to pair with");
    }

    // ── Vault session suppresses gift-pending stash ─────────────────────

    [Fact]
    public void DeleteDuringVaultSession_DoesNotStashGiftPending()
    {
        // Banker NPC that also accepts gifts: open vault, then deposit, then
        // (hypothetical) positive favor delta. Without the suppression the
        // delete would stash on the gift FSM and the positive delta would
        // fabricate a GiftAccepted referencing the deposited item.
        DispatchInteraction("99999, 7, 1000, True, \"NPC_Banker\"");
        _npc.OnVaultOpened();
        DispatchDeleteItem(555555); // deposit

        _bus.Clear();
        DispatchDeltaFavor("99999, \"NPC_Banker\", 5, True");

        _bus.Published<GiftAccepted>().Should().BeEmpty(
            "items deleted during a vault session are deposits, not gifts");
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
