using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player;
using Arda.World.Player.Events;
using FluentAssertions;
using Arda.Composition;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Smaug.Domain;
using Smaug.State;
using Xunit;
using ArdaSkillEntry = Arda.World.Player.SkillEntry;
using RefSkillEntry = Mithril.Shared.Reference.SkillEntry;

namespace Smaug.Tests;

/// <summary>
/// Post-Arda-migration regression tests for <see cref="VendorIngestionService"/>.
/// Events carry enriched NPC key and favor tier from the Arda Npc handler.
/// </summary>
public sealed class VendorIngestionServiceL1Tests
{
    private static readonly DateTimeOffset TestTimestamp =
        new(2026, 5, 19, 22, 27, 48, TimeSpan.Zero);

    private static LogLineMetadata Meta(bool isReplay = false) =>
        new(TestTimestamp, TestTimestamp, IsReplay: isReplay);

    [Fact]
    public async Task ByteEquivalence_ReplayAndLive_ProduceSameObservations()
    {
        var passOne = await RunWith(bus =>
        {
            bus.Publish(new VendorItemSold(130, "BottleOfWater", 78177652, "NPC_Therese", "Neutral", Meta()));
        });

        var passTwo = await RunWith(bus =>
        {
            bus.Publish(new VendorItemSold(130, "BottleOfWater", 78177652, "NPC_Therese", "Neutral", Meta(isReplay: true)));
        });

        passOne.Should().HaveCount(1);
        passOne[0].NpcKey.Should().Be("NPC_Therese");
        passOne[0].InternalName.Should().Be("BottleOfWater");
        passOne[0].PricePaid.Should().Be(130);
        passOne[0].FavorTier.Should().Be("Neutral");

        passTwo.Should().BeEquivalentTo(passOne,
            because: "the same event sequence, regardless of replay/live metadata, must yield identical sink state");
    }

    [Fact]
    public async Task MultipleSells_DifferentNpcs_AllRecorded()
    {
        var obs = await RunWith(bus =>
        {
            bus.Publish(new VendorItemSold(130, "BottleOfWater", 78177652, "NPC_Therese", "Neutral", Meta()));
            bus.Publish(new VendorItemSold(250, "BottleOfWater", 78177653, "NPC_Johen", "Friendly", Meta()));
        });

        obs.Should().HaveCount(2);
        obs[0].NpcKey.Should().Be("NPC_Therese");
        obs[0].PricePaid.Should().Be(130);
        obs[1].NpcKey.Should().Be("NPC_Johen");
        obs[1].PricePaid.Should().Be(250);
    }

    [Fact]
    public async Task ItemSold_WithoutNpcKey_Skipped()
    {
        var obs = await RunWith(bus =>
        {
            bus.Publish(new VendorItemSold(130, "BottleOfWater", 78177652, null, null, Meta()));
        });

        obs.Should().BeEmpty(because: "events without NPC context should be dropped");
    }

    private static async Task<IReadOnlyList<PriceObservation>> RunWith(Action<FakeDomainEventSubscriber> arrange)
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_arda_l1");
        try
        {
            var refData = new FakeRefData();
            var session = new FakeSessionComposer("char|2026-05-19T20:00:00Z");
            var calibration = new PriceCalibrationService(refData, dir, session: session);
            var bus = new FakeDomainEventSubscriber();
            var playerState = new FakePlayerState();

            var service = new VendorIngestionService(bus, calibration, playerState);
            await service.StartAsync(CancellationToken.None);

            arrange(bus);

            await service.StopAsync(CancellationToken.None);
            service.Dispose();

            return calibration.Data.Observations.ToArray();
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class FakeDomainEventSubscriber : IDomainEventSubscriber
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            var type = typeof(T);
            if (!_handlers.TryGetValue(type, out var list))
            {
                list = new List<Delegate>();
                _handlers[type] = list;
            }
            list.Add(handler);
            return new Unsub(() => list.Remove(handler));
        }

        public void Publish<T>(T evt) where T : struct
        {
            if (!_handlers.TryGetValue(typeof(T), out var list)) return;
            foreach (var h in list)
                ((Action<T>)h)(evt);
        }

        private sealed class Unsub(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }

    private sealed class FakePlayerState : IPlayerProgressionState
    {
        public IReadOnlyDictionary<string, EnrichedSkill> Skills { get; } =
            new Dictionary<string, EnrichedSkill>();
        public IReadOnlyDictionary<string, int> RecipeCompletions { get; } =
            new Dictionary<string, int>();
#pragma warning disable CS0067
        public event Action? StateChanged;
#pragma warning restore CS0067
    }

    private sealed class FakeSessionComposer : ISessionComposer
    {
        public ComposedSession? Current { get; }
        public FakeSessionComposer(string sessionId)
        {
            Current = new ComposedSession("char", null,
                new DateTimeOffset(2026, 5, 19, 20, 0, 0, TimeSpan.Zero),
                TimeSpan.Zero, sessionId);
        }
    }

    private sealed class FakeRefData : IReferenceDataService
    {
        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, Item> Items { get; }
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; }
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, RefSkillEntry> Skills { get; } = new Dictionary<string, RefSkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; }
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public FakeRefData()
        {
            var water = new Item
            {
                Id = 1,
                Name = "Bottle of Water",
                InternalName = "BottleOfWater",
                MaxStackSize = 10,
                IconId = 0,
                Keywords = [new ItemKeyword("Drink", 0)],
                Value = 11m,
            };
            Items = new Dictionary<long, Item> { [1] = water };
            ItemsByInternalName = new Dictionary<string, Item>(StringComparer.Ordinal)
            {
                ["BottleOfWater"] = water,
            };
            Npcs = new Dictionary<string, NpcEntry>(StringComparer.Ordinal)
            {
                ["NPC_Therese"] = new NpcEntry(
                    Key: "NPC_Therese",
                    Name: "Therese",
                    Area: "",
                    Preferences: Array.Empty<NpcPreference>(),
                    ItemGiftTiers: Array.Empty<string>(),
                    Services: Array.Empty<NpcService>()),
                ["NPC_Johen"] = new NpcEntry(
                    Key: "NPC_Johen",
                    Name: "Johen",
                    Area: "",
                    Preferences: Array.Empty<NpcPreference>(),
                    ItemGiftTiers: Array.Empty<string>(),
                    Services: Array.Empty<NpcService>()),
            };
        }

        public ReferenceFileSnapshot GetSnapshot(string key) =>
            new("items", ReferenceFileSource.Bundled, "test", null, 1);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
