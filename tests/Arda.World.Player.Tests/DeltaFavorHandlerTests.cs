using System.Collections.Frozen;
using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class DeltaFavorHandlerTests
{
    private readonly SpyBus _bus = new();
    private readonly Npc _npc;
    private readonly DeltaFavorHandler _handler;

    public DeltaFavorHandlerTests()
    {
        var pool = new InternPool(FrozenDictionary<string, string>.Empty);
        _npc = new Npc(_bus, pool);
        _handler = new DeltaFavorHandler(_npc, _bus);
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

    // ── Happy path ───────────────────────────────────────────────────────

    [Fact]
    public void PositiveDelta_DuringNpcInteraction_EmitsDeltaFavorReceived()
    {
        ArmNpcInteraction("NPC_Joe");
        _bus.Clear();

        Dispatch("(12307, \"NPC_Joe\", 25.5, True)");

        var evt = _bus.Published<DeltaFavorReceived>().Should().ContainSingle().Which;
        evt.NpcKey.Should().Be("NPC_Joe");
        evt.Delta.Should().Be(25.5);
    }

    // ── Negative/zero delta ──────────────────────────────────────────────

    [Fact]
    public void ZeroDelta_NoEmission()
    {
        ArmNpcInteraction("NPC_Joe");
        _bus.Clear();

        Dispatch("(12307, \"NPC_Joe\", 0, True)");

        _bus.Published<DeltaFavorReceived>().Should().BeEmpty();
    }

    [Fact]
    public void NegativeDelta_NoEmission()
    {
        ArmNpcInteraction("NPC_Joe");
        _bus.Clear();

        Dispatch("(12307, \"NPC_Joe\", -10.5, True)");

        _bus.Published<DeltaFavorReceived>().Should().BeEmpty();
    }

    // ── No active NPC interaction ────────────────────────────────────────

    [Fact]
    public void NoActiveInteraction_NoEmission()
    {
        Dispatch("(12307, \"NPC_Joe\", 25.5, True)");

        _bus.Published<DeltaFavorReceived>().Should().BeEmpty();
    }

    // ── NPC key mismatch ─────────────────────────────────────────────────

    [Fact]
    public void NpcKeyMismatch_NoEmission()
    {
        ArmNpcInteraction("NPC_Joe");
        _bus.Clear();

        Dispatch("(12307, \"NPC_Other\", 25.5, True)");

        _bus.Published<DeltaFavorReceived>().Should().BeEmpty();
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
