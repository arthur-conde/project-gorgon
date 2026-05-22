using FluentAssertions;
using Mithril.GameState.Inventory;
using Mithril.WorldSim;
using Xunit;

namespace Mithril.GameState.Tests.Inventory;

/// <summary>
/// Tests for the world-sim inventory split (#602) — the ChatWorld half.
/// Pins folder-level semantics for chat-side stack-size observations against
/// the new <see cref="IFolder{T}"/> surface.
/// </summary>
public sealed class ChatInventoryStateServiceTests
{
    private static DateTime Ts(int s) => new(2026, 5, 22, 8, 0, s, DateTimeKind.Utc);
    private static Frame<ChatInventoryObservationFrame> F(string name, int count, DateTime ts) =>
        new(new DateTimeOffset(ts, TimeSpan.Zero), new ChatInventoryObservationFrame(name, count));

    private sealed class StubClock : IWorldClock
    {
        public DateTimeOffset Now => DateTimeOffset.UtcNow;
        public long Frame => 0;
        public WorldMode Mode => WorldMode.Live;
    }

    [Fact]
    public void Observation_emits_ChatInventoryObserved()
    {
        var folder = new ChatInventoryStateService();
        var changes = folder.Apply(F("Egg", 5, Ts(1)), new StubClock());

        changes.Should().ContainSingle();
        var ev = changes[0].Should().BeOfType<ChatInventoryObserved>().Subject;
        ev.DisplayName.Should().Be("Egg");
        ev.Count.Should().Be(5);
        ev.Timestamp.Should().Be(Ts(1));
    }

    [Fact]
    public void TryGetLastObservation_returns_most_recent_per_name()
    {
        var folder = new ChatInventoryStateService();
        folder.Apply(F("Egg", 3, Ts(1)), new StubClock());
        folder.Apply(F("Guava", 2, Ts(2)), new StubClock());
        folder.Apply(F("Egg", 5, Ts(3)), new StubClock());

        folder.TryGetLastObservation("Egg", out var c, out var ts).Should().BeTrue();
        c.Should().Be(5);
        ts.Should().Be(Ts(3));

        folder.TryGetLastObservation("Guava", out c, out ts).Should().BeTrue();
        c.Should().Be(2);

        folder.TryGetLastObservation("Unknown", out _, out _).Should().BeFalse();
    }
}
