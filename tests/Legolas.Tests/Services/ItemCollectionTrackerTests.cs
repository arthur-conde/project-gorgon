using System.IO;
using FluentAssertions;
using Arda.Abstractions.Logs;
using Arda.World.Player.Events;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Mithril.Reference.Models.Items;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

namespace Legolas.Tests.Services;

/// <summary>
/// Post-Arda migration: the tracker subscribes to <see cref="InventoryItemAdded"/>
/// and <see cref="ScreenTextObserved"/> via <see cref="Arda.Contracts.IDomainEventSubscriber"/>.
/// Tests publish events through a <see cref="TestDomainEventBus"/>.
/// </summary>
public sealed class ItemCollectionTrackerTests
{
    private static readonly LogLineMetadata LiveMeta = new(
        Timestamp: new DateTimeOffset(new DateTime(2026, 5, 22, 14, 0, 0, DateTimeKind.Utc), TimeSpan.Zero),
        ReadOn: DateTimeOffset.UtcNow,
        IsReplay: false);

    private static readonly LogLineMetadata ReplayMeta = new(
        Timestamp: new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero),
        ReadOn: DateTimeOffset.UtcNow,
        IsReplay: true);

    private sealed record Harness(
        ItemCollectionTracker Service,
        TestDomainEventBus Bus,
        SessionState Session,
        SurveyFlowController Flow,
        LegolasSettings Settings,
        DiagnosticsLoggerProvider LogProvider);

    private static Harness Build()
    {
        var bus = new TestDomainEventBus();
        var session = new SessionState();
        var settings = new LegolasSettings { AutoResetWhenAllCollected = false };
        var flow = new SurveyFlowController(session, settings);
        var logProvider = new DiagnosticsLoggerProvider(
            Path.Combine(Path.GetTempPath(), "legolas-tracker-" + Guid.NewGuid()));
        var refData = new FakeRefData();
        var svc = new ItemCollectionTracker(
            bus, session, flow,
            refData: refData, logger: logProvider.CreateLogger("Legolas.Ingestion"));
        return new Harness(svc, bus, session, flow, settings, logProvider);
    }

    private static SurveyItemViewModel SeedSurvey(SessionState session, string displayName)
    {
        var vm = new SurveyItemViewModel(Survey.Create(displayName, new MetreOffset(0, 0), gridIndex: 0));
        session.Surveys.Add(vm);
        return vm;
    }

    private static async Task Run(Harness h, Func<Task> body)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await h.Service.StartAsync(cts.Token);
        try { await body(); }
        finally
        {
            await cts.CancelAsync();
            try { await h.Service.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
            h.Service.Dispose();
        }
    }

    // ---- basic add + collect flow ----------------------------------------

    [Fact]
    public async Task Matched_add_then_collect_credits_one()
    {
        var h = Build();
        await Run(h, async () =>
        {
            var survey = SeedSurvey(h.Session, "Iron Ore");

            h.Bus.Publish(new InventoryItemAdded(100, "IronOre", LiveMeta));
            h.Bus.Publish(new ScreenTextObserved(
                "ImportantInfo".AsMemory(),
                "Iron Ore collected!".AsMemory(),
                LiveMeta));

            await Task.Yield();
            survey.Collected.Should().BeTrue();
            h.Session.CollectedItems.Should().ContainKey("Iron Ore")
                .WhoseValue.Should().Be(1);
        });
    }

    [Fact]
    public async Task Collect_without_add_credits_zero()
    {
        var h = Build();
        await Run(h, async () =>
        {
            SeedSurvey(h.Session, "Iron Ore");

            h.Bus.Publish(new ScreenTextObserved(
                "ImportantInfo".AsMemory(),
                "Iron Ore collected!".AsMemory(),
                LiveMeta));

            await Task.Yield();
            h.Session.CollectedItems.Should().NotContainKey("Iron Ore");
        });
    }

    [Fact]
    public async Task Speed_bonus_is_credited_separately()
    {
        var h = Build();
        await Run(h, async () =>
        {
            SeedSurvey(h.Session, "Iron Ore");

            h.Bus.Publish(new InventoryItemAdded(100, "IronOre", LiveMeta));
            h.Bus.Publish(new InventoryItemAdded(101, "CopperOre", LiveMeta));
            h.Bus.Publish(new ScreenTextObserved(
                "ImportantInfo".AsMemory(),
                "Iron Ore collected! Also found Copper Ore x2 (speed bonus!)".AsMemory(),
                LiveMeta));

            await Task.Yield();
            h.Session.CollectedItems.Should().ContainKey("Iron Ore").WhoseValue.Should().Be(1);
            h.Session.CollectedItems.Should().ContainKey("Copper Ore").WhoseValue.Should().Be(1);
        });
    }

    [Fact]
    public async Task Replay_events_are_dropped()
    {
        var h = Build();
        await Run(h, async () =>
        {
            SeedSurvey(h.Session, "Iron Ore");

            h.Bus.Publish(new InventoryItemAdded(100, "IronOre", ReplayMeta));
            h.Bus.Publish(new ScreenTextObserved(
                "ImportantInfo".AsMemory(),
                "Iron Ore collected!".AsMemory(),
                ReplayMeta));

            await Task.Yield();
            h.Session.CollectedItems.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Non_ImportantInfo_category_is_ignored()
    {
        var h = Build();
        await Run(h, async () =>
        {
            SeedSurvey(h.Session, "Iron Ore");
            h.Bus.Publish(new InventoryItemAdded(100, "IronOre", LiveMeta));

            h.Bus.Publish(new ScreenTextObserved(
                "GeneralInfo".AsMemory(),
                "Iron Ore collected!".AsMemory(),
                LiveMeta));

            await Task.Yield();
            h.Session.CollectedItems.Should().BeEmpty();
        });
    }

    // ---- survey-session lifecycle ----------------------------------------

    [Fact]
    public async Task Reset_clears_pending_adds()
    {
        var h = Build();
        await Run(h, async () =>
        {
            h.Bus.Publish(new InventoryItemAdded(100, "IronOre", LiveMeta));
            h.Flow.Reset();

            h.Bus.Publish(new ScreenTextObserved(
                "ImportantInfo".AsMemory(),
                "Iron Ore collected!".AsMemory(),
                LiveMeta));

            await Task.Yield();
            h.Session.CollectedItems.Should().NotContainKey("Iron Ore",
                "pending Adds should be cleared by Reset");
        });
    }

    // ---- helpers ----------------------------------------------------------

    private sealed class FakeRefData : IReferenceDataService
    {
        private static readonly Item _ironOre = new()
        {
            Id = 1, Name = "Iron Ore", InternalName = "IronOre",
            MaxStackSize = 1000, IconId = 0, Keywords = [],
        };
        private static readonly Item _copperOre = new()
        {
            Id = 2, Name = "Copper Ore", InternalName = "CopperOre",
            MaxStackSize = 1000, IconId = 0, Keywords = [],
        };
        private static readonly Item _garnet = new()
        {
            Id = 3, Name = "Garnet", InternalName = "Garnet",
            MaxStackSize = 1000, IconId = 0, Keywords = [],
        };

        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>
        {
            [1L] = _ironOre, [2L] = _copperOre, [3L] = _garnet,
        };
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>(StringComparer.Ordinal)
        {
            ["IronOre"] = _ironOre,
            ["CopperOre"] = _copperOre,
            ["Garnet"] = _garnet,
        };
        public ItemKeywordIndex KeywordIndex => new(Items);
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Recipes.Recipe> Recipes { get; }
            = new Dictionary<string, Mithril.Reference.Models.Recipes.Recipe>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Recipes.Recipe> RecipesByInternalName { get; }
            = new Dictionary<string, Mithril.Reference.Models.Recipes.Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; }
            = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; }
            = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }

}
