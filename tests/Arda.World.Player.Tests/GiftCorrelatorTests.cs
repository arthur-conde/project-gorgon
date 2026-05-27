using System.Collections.Frozen;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

/// <summary>
/// Tests the gift correlation FSM inside <see cref="Npc"/>. The FSM pairs
/// <c>ProcessDeleteItem</c> and <c>ProcessDeltaFavor</c> (in either order)
/// to emit <see cref="GiftAccepted"/>. Replaces the former
/// <c>GiftCorrelatorTests</c> which drove the same logic through a
/// separate bus subscriber.
/// </summary>
public class NpcGiftCorrelationTests
{
    private readonly SpyBus _bus = new();
    private readonly Npc _npc;

    public NpcGiftCorrelationTests()
    {
        var pool = new InternPool(FrozenDictionary<string, string>.Empty);
        _npc = new Npc(_bus, pool);
    }

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    private void ArmNpcInteraction(string npcKey, long entityId = 12307)
    {
        _npc.OnStartInteraction($"({entityId}, 7, 2405.813, True, \"{npcKey}\")".AsSpan(), default, $"LocalPlayer: ProcessStartInteraction({entityId}, 7, 2405.813, True, \"{npcKey}\")", Meta());
        _bus.Clear();
    }

    private void DeleteItem(long instanceId) =>
        _npc.OnDeleteItem($"({instanceId})".AsSpan(),
            $"LocalPlayer: ProcessDeleteItem({instanceId})", Meta());

    private void DeltaFavor(string npcKey, double delta) =>
        _npc.OnDeltaFavor($"(12307, \"{npcKey}\", {delta}, True)".AsSpan(), default, $"LocalPlayer: ProcessDeltaFavor(12307, \"{npcKey}\", {delta}, True)", Meta());

    // ── Delete-first correlation ─────────────────────────────────────────

    [Fact]
    public void DeleteFirst_ThenDelta_EmitsGiftAccepted()
    {
        ArmNpcInteraction("NPC_Joe");
        DeleteItem(100);
        DeltaFavor("NPC_Joe", 25.5);

        _bus.Published<GiftAccepted>().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            EntityId = 12307L,
            NpcKey = "NPC_Joe",
            ItemInstanceId = 100L,
            DeltaFavor = 25.5
        });
    }

    // ── Delta-first correlation ──────────────────────────────────────────

    [Fact]
    public void DeltaFirst_ThenDelete_EmitsGiftAccepted()
    {
        ArmNpcInteraction("NPC_Joe");
        DeltaFavor("NPC_Joe", 10.0);
        DeleteItem(200);

        _bus.Published<GiftAccepted>().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            EntityId = 12307L,
            NpcKey = "NPC_Joe",
            ItemInstanceId = 200L,
            DeltaFavor = 10.0
        });
    }

    // ── New interaction clears pending ───────────────────────────────────

    [Fact]
    public void NewInteraction_ClearsPendingDelete()
    {
        ArmNpcInteraction("NPC_Joe");
        DeleteItem(300);

        ArmNpcInteraction("NPC_Other", 9999);
        DeltaFavor("NPC_Joe", 5.0);

        _bus.Published<GiftAccepted>().Should().BeEmpty();
    }

    [Fact]
    public void NewInteraction_ClearsPendingDelta()
    {
        ArmNpcInteraction("NPC_Joe");
        DeltaFavor("NPC_Joe", 15.0);

        ArmNpcInteraction("NPC_Other", 9999);
        DeleteItem(400);

        _bus.Published<GiftAccepted>().Should().BeEmpty();
    }

    // ── NPC key mismatch ─────────────────────────────────────────────────

    [Fact]
    public void DeltaWithWrongNpcKey_NoCorrelation()
    {
        ArmNpcInteraction("NPC_Joe");
        DeleteItem(500);
        DeltaFavor("NPC_Other", 25.0);

        _bus.Published<GiftAccepted>().Should().BeEmpty();
    }

    [Fact]
    public void DeleteWithWrongNpcKey_NoCorrelation()
    {
        ArmNpcInteraction("NPC_Joe");
        DeltaFavor("NPC_Joe", 25.0);

        // Switch to a different NPC, then delete — pending delta was for NPC_Joe
        ArmNpcInteraction("NPC_Other", 8887);
        DeleteItem(600);

        _bus.Published<GiftAccepted>().Should().BeEmpty();
    }

    // ── Sequential gifts ─────────────────────────────────────────────────

    [Fact]
    public void MultipleGiftsInSequence_AllEmitted()
    {
        ArmNpcInteraction("NPC_Joe");

        DeleteItem(700);
        DeltaFavor("NPC_Joe", 10.0);

        DeleteItem(701);
        DeltaFavor("NPC_Joe", 20.0);

        _bus.Published<GiftAccepted>().Should().HaveCount(2);
        _bus.Published<GiftAccepted>()[0].ItemInstanceId.Should().Be(700);
        _bus.Published<GiftAccepted>()[0].DeltaFavor.Should().Be(10.0);
        _bus.Published<GiftAccepted>()[1].ItemInstanceId.Should().Be(701);
        _bus.Published<GiftAccepted>()[1].DeltaFavor.Should().Be(20.0);
    }

    // ── No active interaction ────────────────────────────────────────────

    [Fact]
    public void DeleteWithoutActiveInteraction_NoPending()
    {
        DeleteItem(999);
        DeltaFavor("NPC_Joe", 25.0);

        _bus.Published<GiftAccepted>().Should().BeEmpty();
    }

    [Fact]
    public void DeltaWithoutActiveInteraction_NoPending()
    {
        DeltaFavor("NPC_Joe", 25.0);
        DeleteItem(999);

        _bus.Published<GiftAccepted>().Should().BeEmpty();
    }

    // ── Non-positive delta ───────────────────────────────────────────────

    [Fact]
    public void ZeroDelta_NoCorrelation()
    {
        ArmNpcInteraction("NPC_Joe");
        DeleteItem(100);
        DeltaFavor("NPC_Joe", 0);

        _bus.Published<GiftAccepted>().Should().BeEmpty();
    }

    [Fact]
    public void NegativeDelta_NoCorrelation()
    {
        ArmNpcInteraction("NPC_Joe");
        DeleteItem(100);
        DeltaFavor("NPC_Joe", -5.0);

        _bus.Published<GiftAccepted>().Should().BeEmpty();
    }

    // ── Reset clears pending ─────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsPendingDelete()
    {
        ArmNpcInteraction("NPC_Joe");
        DeleteItem(100);
        _npc.Reset();

        ArmNpcInteraction("NPC_Joe");
        DeltaFavor("NPC_Joe", 25.0);

        _bus.Published<GiftAccepted>().Should().BeEmpty();
    }

    // ── SpyBus ───────────────────────────────────────────────────────────

    private sealed class SpyBus : IDomainEventBus
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
