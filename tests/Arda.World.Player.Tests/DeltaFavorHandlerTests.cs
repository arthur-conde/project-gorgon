using System.Collections.Frozen;
using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

/// <summary>
/// Verifies that <see cref="DeltaFavorHandler"/> is a thin adapter routing
/// <c>ProcessDeltaFavor</c> to <see cref="Npc.OnDeltaFavor"/>. Filtering
/// (positive delta, active NPC, key match) is verified at the Npc level;
/// this test confirms the dispatch wiring works end-to-end.
/// </summary>
public class DeltaFavorHandlerTests
{
    private readonly SpyBus _bus = new();
    private readonly Npc _npc;
    private readonly DeltaFavorHandler _handler;

    public DeltaFavorHandlerTests()
    {
        var pool = new InternPool(FrozenDictionary<string, string>.Empty);
        _npc = new Npc(_bus, pool);
        _handler = new DeltaFavorHandler(_npc);
    }

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    private void ArmNpcInteraction(string npcKey, long entityId = 12307)
    {
        _npc.OnStartInteraction(
            $"({entityId}, 7, 2405.813, True, \"{npcKey}\")".AsSpan(),
            $"LocalPlayer: ProcessStartInteraction({entityId}, 7, 2405.813, True, \"{npcKey}\")",
            Meta());
    }

    private void Dispatch(string args)
    {
        var line = $"LocalPlayer: ProcessDeltaFavor{args}";
        _handler.Handle(args.AsSpan(), line, Meta());
    }

    [Fact]
    public void RoutesToNpc_DeleteFirstCorrelation()
    {
        ArmNpcInteraction("NPC_Joe");
        _bus.Clear();

        _npc.OnDeleteItem("(100)".AsSpan(), "LocalPlayer: ProcessDeleteItem(100)", Meta());
        Dispatch("(12307, \"NPC_Joe\", 25.5, True)");

        var evt = _bus.Published<GiftAccepted>().Should().ContainSingle().Which;
        evt.NpcKey.Should().Be("NPC_Joe");
        evt.ItemInstanceId.Should().Be(100);
        evt.DeltaFavor.Should().Be(25.5);
    }

    [Fact]
    public void NoActiveInteraction_NoEvent()
    {
        Dispatch("(12307, \"NPC_Joe\", 25.5, True)");

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
