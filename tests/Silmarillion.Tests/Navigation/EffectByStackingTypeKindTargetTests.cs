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
using PocoEffect = Mithril.Reference.Models.Effects.Effect;

namespace Silmarillion.Tests.Navigation;

public sealed class EffectByStackingTypeKindTargetTests
{
    [Fact]
    public void Kind_IsEffectByStackingType()
    {
        var (target, _) = Build();
        target.Kind.Should().Be(EntityKind.EffectByStackingType);
    }

    [Fact]
    public void TabIndex_IsFive()
    {
        var (target, _) = Build();
        target.TabIndex.Should().Be(5);
    }

    [Fact]
    public void TryOpenInWindow_ReturnsFalse()
    {
        var (target, _) = Build();
        target.TryOpenInWindow().Should().BeFalse();
    }

    [Fact]
    public void TrySelectByInternalName_SetsStackingTypeEqualsFilter()
    {
        var (target, vm) = Build();

        var ok = target.TrySelectByInternalName("Food");

        ok.Should().BeTrue();
        vm.QueryText.Should().Be("StackingType = \"Food\"");
    }

    [Fact]
    public void TrySelectByInternalName_EmptyValue_ReturnsFalse_VmUnchanged()
    {
        var (target, vm) = Build();

        target.TrySelectByInternalName("").Should().BeFalse();
        vm.QueryText.Should().BeEmpty();
    }

    [Fact]
    public void TrySelectByInternalName_ClearsPriorSelection_SoFilteredListHasNoStaleRow()
    {
        var (target, vm) = Build(
            ("effect_1", new PocoEffect { InternalName = "effect_1", Name = "X", IconId = 1, StackingType = "Food" }));
        vm.SelectedRow = vm.AllEffects.Single();

        target.TrySelectByInternalName("Food").Should().BeTrue();

        vm.SelectedRow.Should().BeNull(because: "filter-only navigation must drop any stale detail selection");
    }

    private static (EffectByStackingTypeKindTarget Target, EffectsTabViewModel Vm) Build(
        params (string Key, PocoEffect Effect)[] effects)
    {
        var refData = new FakeReferenceData();
        foreach (var (key, eff) in effects) refData.AddEffect(key, eff);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var settings = new SilmarillionSettings();
        var vm = new EffectsTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData), settings);
        return (new EffectByStackingTypeKindTarget(vm), vm);
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, PocoEffect> _effectsByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PocoEffect> _effectsByInternalName = new(StringComparer.Ordinal);

        public void AddEffect(string key, PocoEffect effect)
        {
            _effectsByKey[key] = effect;
            if (!string.IsNullOrEmpty(effect.InternalName))
                _effectsByInternalName[effect.InternalName!] = effect;
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

        public IReadOnlyDictionary<string, PocoEffect> Effects => _effectsByKey;
        public IReadOnlyDictionary<string, PocoEffect> EffectsByInternalName => _effectsByInternalName;

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
