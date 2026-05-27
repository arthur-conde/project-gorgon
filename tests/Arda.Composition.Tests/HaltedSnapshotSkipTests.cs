using Arda.Abstractions.Logs;
using Arda.Composition.Events;
using Arda.Composition.Internal;
using Arda.Contracts;
using Arda.Contracts.State.Health;
using Arda.Dispatch;
using Arda.World.Player.Events;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.Shared.Character;
using Xunit;

namespace Arda.Composition.Tests;

public class HaltedSnapshotSkipTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arda-halt-snapshot-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset BaseTime = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    public HaltedSnapshotSkipTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Halt_SkipsInventoryComposerSnapshotWrite()
    {
        var signal = new TestGrammarSignal();
        var store = new PerCharacterStore<AccumulatorSnapshot>(
            _root, "inventory-accumulator.json",
            AccumulatorSnapshotJsonContext.Default.AccumulatorSnapshot);

        var bus = new DomainEventBus(NullLogger<DomainEventBus>.Instance);
        var composer = new InventoryComposer(bus, store, signal);

        // Accumulate some state by triggering a session-establish + add.
        var session = new ComposedSession("Alice", "Serbule", BaseTime, TimeSpan.Zero, "alice-session");
        bus.Publish(new SessionEstablished(session, Meta(BaseTime)));
        bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(BaseTime.AddMilliseconds(50))));

        // Pre-halt: a session switch should flush the prior session's snapshot.
        signal.RaiseBreak("ProcessAddItem", "boom");

        // Now switching session would flush — but halt suppresses it.
        var nextSession = new ComposedSession("Bob", "Serbule", BaseTime, TimeSpan.Zero, "bob-session");
        bus.Publish(new SessionEstablished(nextSession, Meta(BaseTime.AddSeconds(1))));

        composer.Dispose();

        var aliceFile = Path.Combine(_root, PerCharacterStore<AccumulatorSnapshot>.Slug("Alice", "Serbule"), "inventory-accumulator.json");
        File.Exists(aliceFile).Should().BeFalse(
            "no snapshot should be written for Alice once the halt signal is raised — the prior on-disk file (if any) must remain untouched");
    }

    [Fact]
    public void NoHalt_FlushesInventoryComposerSnapshotOnSessionSwitch()
    {
        var signal = new TestGrammarSignal();
        var store = new PerCharacterStore<AccumulatorSnapshot>(
            _root, "inventory-accumulator.json",
            AccumulatorSnapshotJsonContext.Default.AccumulatorSnapshot);

        var bus = new DomainEventBus(NullLogger<DomainEventBus>.Instance);
        var composer = new InventoryComposer(bus, store, signal);

        var session = new ComposedSession("Alice", "Serbule", BaseTime, TimeSpan.Zero, "alice-session");
        bus.Publish(new SessionEstablished(session, Meta(BaseTime)));
        bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(BaseTime.AddMilliseconds(50))));

        var nextSession = new ComposedSession("Bob", "Serbule", BaseTime, TimeSpan.Zero, "bob-session");
        bus.Publish(new SessionEstablished(nextSession, Meta(BaseTime.AddSeconds(1))));

        composer.Dispose();

        var aliceFile = Path.Combine(_root, PerCharacterStore<AccumulatorSnapshot>.Slug("Alice", "Serbule"), "inventory-accumulator.json");
        File.Exists(aliceFile).Should().BeTrue("baseline: snapshot is written when no halt is in flight");
    }

    private static LogLineMetadata Meta(DateTimeOffset readOn) =>
        new(Timestamp: readOn, ReadOn: readOn, IsReplay: false);

    private sealed class TestGrammarSignal : IGrammarBreakSignal
    {
        public GrammarBreak? Current { get; private set; }
        public bool IsRaised => Current is not null;
        public event EventHandler? Raised;
        public void Raise(GrammarBreak breakDetails)
        {
            Current ??= breakDetails;
            Raised?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseBreak(string verb, string hint) =>
            Raise(new GrammarBreak("Player", verb, "src", "tok", hint, DateTimeOffset.UtcNow));
    }
}
