using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

public sealed class QuestsTabViewModelTests
{
    [Fact]
    public void AllQuests_PopulatedFromReferenceData_OrderedByName()
    {
        var refData = new StubReferenceData
        {
            QuestsByInternalNameMap =
            {
                ["GetCatEyeballs"] = new Quest { InternalName = "GetCatEyeballs", Name = "Get Cat Eyeballs" },
                ["KillSkeletons"] = new Quest { InternalName = "KillSkeletons", Name = "Kill Skeletons" },
            },
        };
        var vm = BuildVm(refData);

        vm.AllQuests.Should().HaveCount(2);
        vm.AllQuests.Select(q => q.Name).Should().Equal("Get Cat Eyeballs", "Kill Skeletons");
    }

    [Fact]
    public void QuestListRow_Name_FallsBackToInternalName_WhenPocoNameMissing()
    {
        var refData = new StubReferenceData
        {
            QuestsByInternalNameMap =
            {
                ["Nameless"] = new Quest { InternalName = "Nameless", Name = null },
            },
        };
        var vm = BuildVm(refData);

        vm.AllQuests.Single().Name.Should().Be("Nameless");
    }

    [Fact]
    public void QuestListRow_FavorNpcDisplayName_ResolvesThroughNameResolver()
    {
        var refData = new StubReferenceData
        {
            QuestsByInternalNameMap =
            {
                ["Q1"] = new Quest { InternalName = "Q1", Name = "Q1", FavorNpc = "NPC_Joeh" },
            },
            NpcsByInternalNameMap =
            {
                ["NPC_Joeh"] = new Npc { Name = "Joeh" },
            },
        };
        var vm = BuildVm(refData);

        vm.AllQuests.Single().FavorNpcDisplayName.Should().Be("Joeh",
            because: "the row pre-resolves FavorNpc internal-name through IEntityNameResolver");
    }

    [Fact]
    public void QuestListRow_FavorNpcDisplayName_IsNullWhenQuestHasNoFavorNpc()
    {
        var refData = new StubReferenceData
        {
            QuestsByInternalNameMap = { ["Q1"] = new Quest { InternalName = "Q1", Name = "Q1" } },
        };
        var vm = BuildVm(refData);

        vm.AllQuests.Single().FavorNpcDisplayName.Should().BeNull();
    }

    [Fact]
    public void QuestListRow_Keywords_WrappedForCollectionContainsQuery()
    {
        // Keywords are exposed as IReadOnlyList<QuestKeywordValue> for the query engine's
        // CONTAINS path (IQueryStringValue, #261). Each wrapper exposes the raw tag as
        // QueryStringValue — what the parser matches against in `Keywords CONTAINS 'X'`.
        var refData = new StubReferenceData
        {
            QuestsByInternalNameMap =
            {
                ["Q1"] = new Quest
                {
                    InternalName = "Q1", Name = "Q1",
                    Keywords = new[] { "MainStory", "DailyQuest" },
                },
            },
        };
        var vm = BuildVm(refData);

        var row = vm.AllQuests.Single();
        row.Keywords.Should().HaveCount(2);
        row.Keywords.Select(k => k.QueryStringValue).Should().BeEquivalentTo("MainStory", "DailyQuest");
    }

    [Fact]
    public void QuestListRow_CadenceProjection_FlowsFromClassifier()
    {
        var refData = new StubReferenceData
        {
            QuestsByInternalNameMap =
            {
                ["OneOff"] = new Quest { InternalName = "OneOff", Name = "OneOff" },
                ["DailyHours"] = new Quest { InternalName = "DailyHours", Name = "Daily", ReuseTime_Hours = 20 },
                ["WeeklyDays"] = new Quest { InternalName = "WeeklyDays", Name = "Weekly", ReuseTime_Days = 7 },
            },
        };
        var vm = BuildVm(refData);

        var oneOff = vm.AllQuests.Single(r => r.InternalName == "OneOff");
        oneOff.IsRepeatable.Should().BeFalse();
        oneOff.Cadence.Should().Be(QuestCadence.OneTime);
        oneOff.ReuseMinutes.Should().Be(0);

        var daily = vm.AllQuests.Single(r => r.InternalName == "DailyHours");
        daily.IsRepeatable.Should().BeTrue();
        daily.Cadence.Should().Be(QuestCadence.Daily);
        daily.ReuseMinutes.Should().Be(20 * 60);

        var weekly = vm.AllQuests.Single(r => r.InternalName == "WeeklyDays");
        weekly.IsRepeatable.Should().BeTrue();
        weekly.Cadence.Should().Be(QuestCadence.Weekly);
        weekly.ReuseMinutes.Should().Be(7 * 24 * 60);
    }

    [Fact]
    public void QuestListRow_IsWorkOrder_DerivedFromWorkOrderSkill()
    {
        var refData = new StubReferenceData
        {
            QuestsByInternalNameMap =
            {
                ["Story"] = new Quest { InternalName = "Story", Name = "Story" },
                ["WorkOrder"] = new Quest { InternalName = "WorkOrder", Name = "WO", WorkOrderSkill = "Cooking" },
            },
        };
        var vm = BuildVm(refData);

        vm.AllQuests.Single(r => r.InternalName == "Story").IsWorkOrder.Should().BeFalse();
        vm.AllQuests.Single(r => r.InternalName == "WorkOrder").IsWorkOrder.Should().BeTrue();
    }

    [Fact]
    public void SelectedRow_BuildsDetailViewModel()
    {
        var refData = new StubReferenceData
        {
            QuestsByInternalNameMap =
            {
                ["Q1"] = new Quest { InternalName = "Q1", Name = "Quest One" },
            },
        };
        var vm = BuildVm(refData);

        vm.DetailViewModel.Should().BeNull();
        vm.SelectedRow = vm.AllQuests.Single();
        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.DisplayName.Should().Be("Quest One");
    }

    [Fact]
    public void SelectedRow_SetToNull_ClearsDetailViewModel()
    {
        var refData = new StubReferenceData
        {
            QuestsByInternalNameMap = { ["Q1"] = new Quest { InternalName = "Q1", Name = "Q1" } },
        };
        var vm = BuildVm(refData);
        vm.SelectedRow = vm.AllQuests.Single();

        vm.SelectedRow = null;
        vm.DetailViewModel.Should().BeNull();
    }

    [Fact]
    public void FileUpdated_QuestsRefresh_RebuildsAllQuests_PreservingSelectionByInternalName()
    {
        var refData = new StubReferenceData
        {
            QuestsByInternalNameMap = { ["Q1"] = new Quest { InternalName = "Q1", Name = "Original" } },
        };
        var vm = BuildVm(refData);
        vm.SelectedRow = vm.AllQuests.Single();
        vm.SelectedRow!.Name.Should().Be("Original"); // baseline

        // Swap in a fresh Quest instance for the same key — refData hands out the new instance.
        refData.QuestsByInternalNameMap["Q1"] = new Quest { InternalName = "Q1", Name = "Refreshed" };
        refData.RaiseFileUpdated("quests");

        vm.SelectedRow.Should().NotBeNull(because: "FileUpdated should preserve selection by InternalName");
        vm.SelectedRow!.InternalName.Should().Be("Q1");
        vm.SelectedRow.Name.Should().Be("Refreshed");
    }

    [Fact]
    public void FileUpdated_UnrelatedFile_DoesNotRebuild()
    {
        var refData = new StubReferenceData
        {
            QuestsByInternalNameMap = { ["Q1"] = new Quest { InternalName = "Q1", Name = "Q1" } },
        };
        var vm = BuildVm(refData);
        var originalAllQuests = vm.AllQuests;

        refData.RaiseFileUpdated("attributes");

        vm.AllQuests.Should().BeSameAs(originalAllQuests);
    }

    [Fact]
    public void SchemaSnapshot_IncludesQuestListRowProperties_ForQueryBoxCompletion()
    {
        // The reflected schema drives MithrilQueryBox completion and the parser side too —
        // adding a field to QuestListRow without it appearing here breaks autocomplete and
        // unknown-column highlighting silently in tests.
        var columns = QuestsTabViewModel.SchemaSnapshot.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        columns.Should().Contain("Name");
        columns.Should().Contain("Level");
        columns.Should().Contain("Keywords");
        columns.Should().Contain("IsRepeatable");
        columns.Should().Contain("IsGuildQuest");
        columns.Should().Contain("IsCancellable");
        columns.Should().Contain("FavorNpcDisplayName");
        columns.Should().Contain("DisplayedLocation");
    }

    private static QuestsTabViewModel BuildVm(StubReferenceData refData) =>
        new(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<string, Quest> QuestsByInternalNameMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Npc> NpcsByInternalNameMap { get; } = new(StringComparer.Ordinal);

        public void RaiseFileUpdated(string fileKey) => FileUpdated?.Invoke(this, fileKey);

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>(StringComparer.Ordinal);
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName => NpcsByInternalNameMap;
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Quest> Quests => QuestsByInternalNameMap;
        public IReadOnlyDictionary<string, Quest> QuestsByInternalName => QuestsByInternalNameMap;
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated;
    }
}
