using FluentAssertions;
using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class AbilityByEffectKeywordKindTargetTests
{
    [Fact]
    public void Kind_IsAbilityByEffectKeyword()
    {
        var (target, _) = Build();
        target.Kind.Should().Be(EntityKind.AbilityByEffectKeyword);
    }

    [Fact]
    public void TabIndex_IsFour()
    {
        // Lands on the Abilities tab — pre-filtered by EffectKeywordReqs CONTAINS "<tag>".
        var (target, _) = Build();
        target.TabIndex.Should().Be(4);
    }

    [Fact]
    public void TryOpenInWindow_ReturnsFalse()
    {
        var (target, _) = Build();
        target.TryOpenInWindow().Should().BeFalse();
    }

    [Fact]
    public void TrySelectByInternalName_SetsEffectKeywordReqsContainsFilter()
    {
        var (target, vm) = Build();

        var ok = target.TrySelectByInternalName("FrostShard");

        ok.Should().BeTrue();
        vm.QueryText.Should().Be("EffectKeywordReqs CONTAINS \"FrostShard\"");
    }

    [Fact]
    public void TrySelectByInternalName_EmptyKeyword_ReturnsFalse_VmUnchanged()
    {
        var (target, vm) = Build();

        target.TrySelectByInternalName("").Should().BeFalse();
        vm.QueryText.Should().BeEmpty();
    }

    [Fact]
    public void TrySelectByInternalName_ClearsPriorSelection_SoFilteredListHasNoStaleRow()
    {
        var ability = new Ability { InternalName = "Strike", Name = "Strike", Skill = "Sword", Level = 1, EffectKeywordReqs = ["FrostShard"] };
        var (target, vm) = Build(("ability_1", ability));
        vm.SelectedRow = vm.AllAbilities.Single();
        vm.SelectedRow.Should().NotBeNull();

        target.TrySelectByInternalName("FrostShard").Should().BeTrue();

        vm.SelectedRow.Should().BeNull(because: "filter-only navigation must drop any stale detail selection");
    }

    private static (AbilityByEffectKeywordKindTarget Target, AbilitiesTabViewModel Vm) Build(
        params (string Key, Ability Ability)[] abilities)
    {
        var refData = new FakeReferenceData();
        foreach (var (key, ability) in abilities) refData.AddAbility(key, ability);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new AbilitiesTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData));
        return (new AbilityByEffectKeywordKindTarget(vm), vm);
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, Ability> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Ability> _byInternalName = new(StringComparer.Ordinal);

        public void AddAbility(string key, Ability ability)
        {
            _byKey[key] = ability;
            if (!string.IsNullOrEmpty(ability.InternalName))
                _byInternalName[ability.InternalName!] = ability;
        }

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
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
        public IReadOnlyDictionary<string, Quest> Quests { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public IReadOnlyDictionary<string, Ability> Abilities => _byKey;
        public IReadOnlyDictionary<string, Ability> AbilitiesByInternalName => _byInternalName;

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
