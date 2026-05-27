using Arda.Abstractions.Logs;
using Arda.Composition.Events;
using Arda.Composition.Internal;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.Shared.Character;
using Xunit;

namespace Arda.Composition.Tests;

public class InventoryComposerTests : IDisposable
{
    private readonly DomainEventBus _bus = new(NullLogger<DomainEventBus>.Instance);
    private readonly InventoryComposer _composer;
    private readonly List<InventoryItemResolved> _resolved = [];

    private static readonly DateTimeOffset BaseTime = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    public InventoryComposerTests()
    {
        _composer = new InventoryComposer(_bus);
        _bus.Subscribe<InventoryItemResolved>(e => _resolved.Add(e));
    }

    public void Dispose() => _composer.Dispose();

    private static LogLineMetadata Meta(DateTimeOffset readOn) =>
        new(Timestamp: readOn, ReadOn: readOn, IsReplay: false);

    [Fact]
    public void PlayerAdd_ThenChat_EmitsResolved()
    {
        var readOn = BaseTime;
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(readOn)));
        _bus.Publish(new ChatInventoryObserved("Iron Sword", 1, Meta(readOn.AddMilliseconds(100))));

        _resolved.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                InstanceId = 1001L,
                InternalName = "item_sword",
                DisplayName = "Iron Sword",
                Count = 1,
            });
    }

    [Fact]
    public void Chat_ThenPlayerAdd_EmitsResolved()
    {
        var readOn = BaseTime;
        _bus.Publish(new ChatInventoryObserved("Iron Sword", 1, Meta(readOn)));
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(readOn.AddMilliseconds(100))));

        _resolved.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                InstanceId = 1001L,
                InternalName = "item_sword",
                DisplayName = "Iron Sword",
                Count = 1,
            });
    }

    [Fact]
    public void EventsTooFarApart_NoResolution()
    {
        var readOn = BaseTime;
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(readOn)));
        _bus.Publish(new ChatInventoryObserved("Iron Sword", 1, Meta(readOn.AddSeconds(3))));

        _resolved.Should().BeEmpty();
    }

    [Fact]
    public void AtExactWindowBoundary_StillMatches()
    {
        var readOn = BaseTime;
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(readOn)));
        _bus.Publish(new ChatInventoryObserved("Iron Sword", 1, Meta(readOn.AddSeconds(2))));

        _resolved.Should().ContainSingle();
    }

    [Fact]
    public void MultipleAdds_MultipleChatsFIFO_CorrelateInOrder()
    {
        var readOn = BaseTime;

        _bus.Publish(new InventoryItemAdded(1, "item_a", Meta(readOn)));
        _bus.Publish(new InventoryItemAdded(2, "item_b", Meta(readOn.AddMilliseconds(50))));

        _bus.Publish(new ChatInventoryObserved("Alpha", 1, Meta(readOn.AddMilliseconds(100))));
        _bus.Publish(new ChatInventoryObserved("Beta", 2, Meta(readOn.AddMilliseconds(150))));

        _resolved.Should().HaveCount(2);
        _resolved[0].InstanceId.Should().Be(1);
        _resolved[0].DisplayName.Should().Be("Alpha");
        _resolved[1].InstanceId.Should().Be(2);
        _resolved[1].DisplayName.Should().Be("Beta");
    }

    [Fact]
    public void PendingListTrimmed_WhenExceedingMaxPending()
    {
        var readOn = BaseTime;

        for (var i = 0; i < 70; i++)
            _bus.Publish(new InventoryItemAdded(i, $"item_{i}", Meta(readOn.AddMilliseconds(i))));

        // 70 - MaxPending(64) = 6 evicted (items 0-5).
        // A chat within the window matches the oldest *surviving* item (6).
        _bus.Publish(new ChatInventoryObserved("First", 1, Meta(readOn)));

        _resolved.Should().ContainSingle()
            .Which.InstanceId.Should().Be(6, "items 0-5 were trimmed; item 6 is the oldest remaining");
    }

    [Fact]
    public void MaxPendingOverflow_EmitsWarningPerEviction()
    {
        _composer.Dispose(); // discard the default no-logger composer

        var warningCount = 0;
        var logger = new CountingLogger(level => { if (level == LogLevel.Warning) warningCount++; });
        using var composer = new InventoryComposer(_bus, store: null, logger: logger);

        // 64 fill MaxPending; subsequent 64 each evict the oldest.
        for (var i = 0; i < 128; i++)
            _bus.Publish(new InventoryItemAdded(i, $"item_{i}", Meta(BaseTime.AddMilliseconds(i))));

        warningCount.Should().Be(64,
            "each add past MaxPending evicts one uncorrelated entry; every eviction is observable");
    }

    private sealed class CountingLogger(Action<LogLevel> onLog) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => onLog(logLevel);
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [Fact]
    public void Dispose_StopsSubscriptions()
    {
        _composer.Dispose();

        var readOn = BaseTime;
        _bus.Publish(new InventoryItemAdded(1, "item_a", Meta(readOn)));
        _bus.Publish(new ChatInventoryObserved("Alpha", 1, Meta(readOn)));

        _resolved.Should().BeEmpty();
    }

    [Fact]
    public void ExpiredPendingEvents_AreRemovedByTimeTrim()
    {
        var readOn = BaseTime;

        _bus.Publish(new InventoryItemAdded(1, "item_old", Meta(readOn)));

        // Publish a newer add that triggers trimming of the old one
        _bus.Publish(new InventoryItemAdded(2, "item_new", Meta(readOn.AddSeconds(5))));

        // Now a chat arrives in range of the old event's time — but it was expired
        _bus.Publish(new ChatInventoryObserved("Old Item", 1, Meta(readOn.AddMilliseconds(500))));

        _resolved.Should().BeEmpty("the old add was expired by time-based trimming");
    }

    [Fact]
    public void ResolvedEvent_CarriesPlayerMetadata()
    {
        var readOn = BaseTime;
        var playerMeta = Meta(readOn);
        var chatMeta = Meta(readOn.AddMilliseconds(100));

        _bus.Publish(new InventoryItemAdded(1001, "item_sword", playerMeta));
        _bus.Publish(new ChatInventoryObserved("Iron Sword", 1, chatMeta));

        _resolved.Should().ContainSingle()
            .Which.Metadata.Should().Be(playerMeta);
    }

    [Fact]
    public void PersistenceRoundTrip_RestoresAccumulator()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mithril-inv-{Guid.NewGuid():N}");
        try
        {
            var store = new PerCharacterStore<AccumulatorSnapshot>(
                tempDir,
                "inventory-accumulator.json",
                AccumulatorSnapshotJsonContext.Default.AccumulatorSnapshot);

            // composer1 sees player+chat traffic, then disposes (flushes to disk).
            using (var composer1 = new InventoryComposer(_bus, store))
            {
                var session = new ComposedSession("Alice", "Server1",
                    BaseTime, TimeSpan.Zero, "Alice:20260526120000");
                _bus.Publish(new SessionEstablished(session, Meta(BaseTime)));

                _bus.Publish(new InventoryItemAdded(2001, "item_sword", Meta(BaseTime.AddSeconds(1))));
                _bus.Publish(new ChatInventoryObserved("Iron Sword", 1, Meta(BaseTime.AddSeconds(1).AddMilliseconds(50))));
            }

            // composer2 starts cold against the same store; session-establish triggers load.
            using var composer2 = new InventoryComposer(_bus, store);
            var session2 = new ComposedSession("Alice", "Server1",
                BaseTime.AddHours(1), TimeSpan.Zero, "Alice:20260526130000");
            _bus.Publish(new SessionEstablished(session2, Meta(BaseTime.AddHours(1))));

            composer2.Items.Should().ContainKey(2001L);
            composer2.Items[2001L].InternalName.Should().Be("item_sword");
            composer2.Items[2001L].DisplayName.Should().Be("Iron Sword",
                "the chat-side display name resolved before the snapshot was written");
            composer2.Items[2001L].IsRemoved.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void PersistenceRoundTrip_SkipsExpiredSoftDeletes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mithril-inv-{Guid.NewGuid():N}");
        try
        {
            var store = new PerCharacterStore<AccumulatorSnapshot>(
                tempDir,
                "inventory-accumulator.json",
                AccumulatorSnapshotJsonContext.Default.AccumulatorSnapshot);

            // Pre-seed: one soft-deleted item past the 30-day retention window,
            // one within it. Only the in-window one should survive the reload.
            var snapshot = new AccumulatorSnapshot();
            snapshot.Entries[3001L] = new AccumulatorSnapshot.PersistedEntry
            {
                InternalName = "item_old",
                IsRemoved = true,
                RemovedAt = DateTimeOffset.UtcNow.AddDays(-45),
                FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-60),
                LastUpdatedAt = DateTimeOffset.UtcNow.AddDays(-45),
                StackSize = 0,
            };
            snapshot.Entries[3002L] = new AccumulatorSnapshot.PersistedEntry
            {
                InternalName = "item_recent",
                IsRemoved = true,
                RemovedAt = DateTimeOffset.UtcNow.AddDays(-7),
                FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-10),
                LastUpdatedAt = DateTimeOffset.UtcNow.AddDays(-7),
                StackSize = 0,
            };
            store.Save("Alice", "Server1", snapshot);

            using var composer = new InventoryComposer(_bus, store);
            var session = new ComposedSession("Alice", "Server1",
                BaseTime, TimeSpan.Zero, "Alice:20260526120000");
            _bus.Publish(new SessionEstablished(session, Meta(BaseTime)));

            composer.Items.Should().NotContainKey(3001L, "soft-deleted >30d ago is past retention");
            composer.Items.Should().ContainKey(3002L, "soft-deleted within 30d is retained");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
