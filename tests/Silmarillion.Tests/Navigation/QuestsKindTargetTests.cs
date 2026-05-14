using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class QuestsKindTargetTests
{
    [Fact]
    public void Kind_IsQuest()
    {
        var (target, _, _) = BuildTarget();
        target.Kind.Should().Be(EntityKind.Quest);
    }

    [Fact]
    public void TabIndex_IsThree()
    {
        // Items=0, Recipes=1, NPCs=2, Quests=3 — second bucket-B tab after NPCs.
        var (target, _, _) = BuildTarget();
        target.TabIndex.Should().Be(3);
    }

    [Fact]
    public void TrySelectByInternalName_KnownQuest_SelectsOnTabVm_ReturnsTrue()
    {
        var (target, vm, _) = BuildTarget(("quest_1", new Quest { InternalName = "GetCatEyeballs", Name = "Get Cat Eyeballs" }));

        var ok = target.TrySelectByInternalName("GetCatEyeballs");

        ok.Should().BeTrue();
        vm.SelectedRow.Should().NotBeNull();
        vm.SelectedRow!.InternalName.Should().Be("GetCatEyeballs");
        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.DisplayName.Should().Be("Get Cat Eyeballs");
    }

    [Fact]
    public void TrySelectByInternalName_UnknownQuest_ReturnsFalse_VmUnchanged()
    {
        var (target, vm, _) = BuildTarget();
        vm.SelectedRow.Should().BeNull();

        target.TrySelectByInternalName("DoesNotExist").Should().BeFalse();
        vm.SelectedRow.Should().BeNull();
    }

    [Fact]
    public void TryOpenInWindow_NoDetailSelected_ReturnsFalse()
    {
        var (target, _, _) = BuildTarget();
        target.TryOpenInWindow().Should().BeFalse();
    }

    [Fact]
    public void TrySelectByInternalName_ClearsResidualQueryText_SoTargetRowIsVisible()
    {
        var (target, vm, _) = BuildTarget(("quest_1", new Quest { InternalName = "GetCatEyeballs", Name = "Get Cat Eyeballs" }));
        vm.QueryText = "Keywords CONTAINS 'MainStory'";

        var ok = target.TrySelectByInternalName("GetCatEyeballs");

        ok.Should().BeTrue();
        vm.QueryText.Should().BeEmpty(because: "the kind target clears any prior filter before selecting");
        vm.SelectedRow!.InternalName.Should().Be("GetCatEyeballs");
    }

    private static (QuestsKindTarget Target, QuestsTabViewModel Vm, FakeReferenceData RefData) BuildTarget(
        params (string Key, Quest Quest)[] quests)
    {
        var refData = new FakeReferenceData();
        foreach (var (key, quest) in quests)
            refData.AddQuest(key, quest);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new QuestsTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData));
        var target = new QuestsKindTarget(vm);
        return (target, vm, refData);
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, Quest> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Quest> _byInternalName = new(StringComparer.Ordinal);

        public void AddQuest(string key, Quest quest)
        {
            _byKey[key] = quest;
            if (!string.IsNullOrEmpty(quest.InternalName))
                _byInternalName[quest.InternalName!] = quest;
        }

        public IReadOnlyList<string> Keys => Array.Empty<string>();
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>(StringComparer.Ordinal);
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName { get; } = new Dictionary<string, Npc>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Quest> Quests => _byKey;
        public IReadOnlyDictionary<string, Quest> QuestsByInternalName => _byInternalName;
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
