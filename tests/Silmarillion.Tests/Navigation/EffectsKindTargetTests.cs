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

public sealed class EffectsKindTargetTests
{
    [Fact]
    public void Kind_IsEffect()
    {
        var (target, _, _) = BuildTarget();
        target.Kind.Should().Be(EntityKind.Effect);
    }

    [Fact]
    public void TabIndex_IsFive()
    {
        // Items=0, Recipes=1, NPCs=2, Quests=3, Abilities=4, Effects=5 — sixth tab.
        var (target, _, _) = BuildTarget();
        target.TabIndex.Should().Be(5);
    }

    [Fact]
    public void TrySelectByInternalName_KnownEffect_SelectsOnTabVm_ReturnsTrue()
    {
        var (target, vm, _) = BuildTarget(
            ("effect_10003", new PocoEffect { InternalName = "effect_10003", Name = "Sticky!", IconId = 42 }));

        var ok = target.TrySelectByInternalName("effect_10003");

        ok.Should().BeTrue();
        vm.SelectedRow.Should().NotBeNull();
        vm.SelectedRow!.EnvelopeKey.Should().Be("effect_10003");
        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.DisplayName.Should().Be("Sticky!");
    }

    [Fact]
    public void TrySelectByInternalName_UnknownEffect_ReturnsFalse_VmUnchanged()
    {
        var (target, vm, _) = BuildTarget();
        vm.SelectedRow.Should().BeNull();

        target.TrySelectByInternalName("effect_99999").Should().BeFalse();
        vm.SelectedRow.Should().BeNull();
    }

    [Fact]
    public void TrySelectByInternalName_ClearsResidualQueryText_SoTargetRowIsVisible()
    {
        var (target, vm, _) = BuildTarget(
            ("effect_1", new PocoEffect { InternalName = "effect_1", Name = "X", IconId = 1, Keywords = ["Buff"] }));
        vm.QueryText = "Keywords CONTAINS 'Debuff'";

        var ok = target.TrySelectByInternalName("effect_1");

        ok.Should().BeTrue();
        vm.QueryText.Should().BeEmpty(because: "the kind target clears any prior filter before selecting");
        vm.SelectedRow!.EnvelopeKey.Should().Be("effect_1");
    }

    [Fact]
    public void TryOpenInWindow_NoDetailSelected_ReturnsFalse()
    {
        var (target, _, _) = BuildTarget();
        target.TryOpenInWindow().Should().BeFalse();
    }

    private static (EffectsKindTarget Target, EffectsTabViewModel Vm, FakeReferenceData RefData) BuildTarget(
        params (string Key, PocoEffect Effect)[] effects)
    {
        var refData = new FakeReferenceData();
        foreach (var (key, eff) in effects) refData.AddEffect(key, eff);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var settings = new SilmarillionSettings();
        var vm = new EffectsTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData), settings);
        var target = new EffectsKindTarget(vm);
        return (target, vm, refData);
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
