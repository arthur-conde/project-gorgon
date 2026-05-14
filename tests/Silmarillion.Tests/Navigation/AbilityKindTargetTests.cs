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

public sealed class AbilityKindTargetTests
{
    [Fact]
    public void Kind_IsAbility()
    {
        var (target, _, _) = BuildTarget();
        target.Kind.Should().Be(EntityKind.Ability);
    }

    [Fact]
    public void TabIndex_IsFour()
    {
        // Items=0, Recipes=1, NPCs=2, Quests=3, Abilities=4 — third bucket-B tab.
        var (target, _, _) = BuildTarget();
        target.TabIndex.Should().Be(4);
    }

    [Fact]
    public void TrySelectByInternalName_KnownAbility_SelectsOnTabVm_ReturnsTrue()
    {
        var (target, vm, _) = BuildTarget(("ability_1", new Ability { InternalName = "SwordSlash", Name = "Sword Slash", Skill = "Sword", Level = 1 }));

        var ok = target.TrySelectByInternalName("SwordSlash");

        ok.Should().BeTrue();
        vm.SelectedRow.Should().NotBeNull();
        vm.SelectedRow!.InternalName.Should().Be("SwordSlash");
        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.DisplayName.Should().Be("Sword Slash");
    }

    [Fact]
    public void TrySelectByInternalName_UnknownAbility_ReturnsFalse_VmUnchanged()
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
        var (target, vm, _) = BuildTarget(("ability_1", new Ability { InternalName = "SwordSlash", Name = "Sword Slash", Skill = "Sword", Level = 1 }));
        vm.QueryText = "Keywords CONTAINS 'Attack'";

        var ok = target.TrySelectByInternalName("SwordSlash");

        ok.Should().BeTrue();
        vm.QueryText.Should().BeEmpty(because: "the kind target clears any prior filter before selecting");
        vm.SelectedRow!.InternalName.Should().Be("SwordSlash");
    }

    private static (AbilityKindTarget Target, AbilitiesTabViewModel Vm, FakeReferenceData RefData) BuildTarget(
        params (string Key, Ability Ability)[] abilities)
    {
        var refData = new FakeReferenceData();
        foreach (var (key, ability) in abilities)
            refData.AddAbility(key, ability);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new AbilitiesTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData));
        var target = new AbilityKindTarget(vm);
        return (target, vm, refData);
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
