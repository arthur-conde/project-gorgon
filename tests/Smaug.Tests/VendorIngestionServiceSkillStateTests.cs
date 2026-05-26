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
/// Post-Arda-migration tests for <see cref="VendorIngestionService"/>.
/// Events carry enriched NPC key and favor tier from the Arda Npc handler.
/// Verifies the CivicPride on-demand read from <see cref="IPlayerState"/>.
/// </summary>
public sealed class VendorIngestionServiceSkillStateTests
{
    private static readonly DateTimeOffset TestTimestamp =
        new(2026, 5, 19, 22, 27, 48, TimeSpan.Zero);

    private static readonly LogLineMetadata LiveMeta =
        new(TestTimestamp, TestTimestamp, IsReplay: false);

    [Fact]
    public async Task CivicPride_ReadOnDemandFromPlayerState()
    {
        await using var harness = await TestHarness.StartAsync();

        harness.PlayerState.SetSkill("CivicPride", 15);

        harness.Bus.Publish(new VendorItemSold(130, "BottleOfWater", 78177652, "NPC_Therese", "Neutral", LiveMeta));

        harness.Calibration.Data.Observations.Should().HaveCount(1);
        harness.Calibration.Data.Observations[0].CivicPrideLevel.Should().Be(15,
            because: "CivicPride should be read from IPlayerProgressionState.Skills[\"CivicPride\"].Level on demand");
    }

    [Fact]
    public async Task NoCivicPride_DefaultsToZero()
    {
        await using var harness = await TestHarness.StartAsync();

        harness.Bus.Publish(new VendorItemSold(130, "BottleOfWater", 78177652, "NPC_Therese", "Neutral", LiveMeta));

        harness.Calibration.Data.Observations.Should().HaveCount(1);
        harness.Calibration.Data.Observations[0].CivicPrideLevel.Should().Be(0);
    }

    [Fact]
    public async Task FullSellSequence_RecordsObservation()
    {
        await using var harness = await TestHarness.StartAsync();

        harness.Bus.Publish(new VendorItemSold(130, "BottleOfWater", 78177652, "NPC_Therese", "Neutral", LiveMeta));

        harness.Calibration.Data.Observations.Should().HaveCount(1);
        var obs = harness.Calibration.Data.Observations[0];
        obs.NpcKey.Should().Be("NPC_Therese");
        obs.InternalName.Should().Be("BottleOfWater");
        obs.PricePaid.Should().Be(130);
        obs.FavorTier.Should().Be("Neutral");
    }

    [Fact]
    public async Task SellWithoutContext_IsSkipped()
    {
        await using var harness = await TestHarness.StartAsync();

        harness.Bus.Publish(new VendorItemSold(130, "BottleOfWater", 78177652, null, null, LiveMeta));

        harness.Calibration.Data.Observations.Should().BeEmpty(
            because: "a sell without NPC context (null NpcKey) should be dropped");
    }

    [Fact]
    public async Task SellWithEmptyNpcKey_IsSkipped()
    {
        await using var harness = await TestHarness.StartAsync();

        harness.Bus.Publish(new VendorItemSold(130, "BottleOfWater", 78177652, "", "Neutral", LiveMeta));

        harness.Calibration.Data.Observations.Should().BeEmpty(
            because: "a sell with empty NpcKey should be dropped");
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        public VendorIngestionService Service { get; }
        public FakeDomainEventSubscriber Bus { get; }
        public FakePlayerState PlayerState { get; }
        public PriceCalibrationService Calibration { get; }
        private readonly string _tempDir;

        private TestHarness(
            VendorIngestionService service,
            FakeDomainEventSubscriber bus,
            FakePlayerState playerState,
            PriceCalibrationService calibration,
            string tempDir)
        {
            Service = service;
            Bus = bus;
            PlayerState = playerState;
            Calibration = calibration;
            _tempDir = tempDir;
        }

        public static async Task<TestHarness> StartAsync()
        {
            var tempDir = Mithril.TestSupport.TestPaths.CreateTempDir("smaug_arda_test");
            var refData = new FakeRefData();
            var session = new FakeSessionComposer("char|2026-05-19T20:00:00Z");
            var calibration = new PriceCalibrationService(refData, tempDir, session: session);
            var bus = new FakeDomainEventSubscriber();
            var playerState = new FakePlayerState();

            var service = new VendorIngestionService(bus, calibration, playerState);

            await service.StartAsync(CancellationToken.None);

            return new TestHarness(service, bus, playerState, calibration, tempDir);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Service.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { Service.Dispose(); } catch { }
            try { System.IO.Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
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
        private readonly Dictionary<string, EnrichedSkill> _skills = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, EnrichedSkill> Skills => _skills;
        public IReadOnlyDictionary<string, int> RecipeCompletions { get; } = new Dictionary<string, int>();
#pragma warning disable CS0067
        public event Action? StateChanged;
#pragma warning restore CS0067

        public void SetSkill(string name, int level) =>
            _skills[name] = new EnrichedSkill(name, level, 0, 0, 0, 0, false, DateTimeOffset.UtcNow);
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
