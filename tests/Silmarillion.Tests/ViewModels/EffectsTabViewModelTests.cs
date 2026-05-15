using FluentAssertions;
using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Misc;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;
using PocoEffect = Mithril.Reference.Models.Effects.Effect;

namespace Silmarillion.Tests.ViewModels;

file static class NavFactory
{
    public static SilmarillionReferenceNavigator WithKinds(params EntityKind[] kinds) =>
        new(kinds.Select(k => (IReferenceKindTarget)new StubKindTarget(k)));

    private sealed class StubKindTarget : IReferenceKindTarget
    {
        public StubKindTarget(EntityKind kind) => Kind = kind;
        public EntityKind Kind { get; }
        public int TabIndex => 0;
        public bool TrySelectByInternalName(string internalName) => true;
        public bool TryOpenInWindow() => false;
    }
}

public sealed class EffectsTabViewModelTests
{
    [Fact]
    public void AllEffects_PopulatedFromReferenceData_OrderedByDisplayName()
    {
        var refData = new StubReferenceData
        {
            EffectsByKey =
            {
                ["effect_1"] = new PocoEffect { InternalName = "effect_1", Name = "Bravery", IconId = 1 },
                ["effect_2"] = new PocoEffect { InternalName = "effect_2", Name = "Aegis", IconId = 2 },
            },
        };

        var vm = BuildVm(refData);

        vm.AllEffects.Should().HaveCount(2);
        vm.AllEffects.Select(r => r.DisplayName).Should().Equal("Aegis", "Bravery");
    }

    [Fact]
    public void EffectListRow_NameFallsBackToEnvelopeKey_WhenPocoNameIsEmpty()
    {
        var refData = new StubReferenceData
        {
            EffectsByKey = { ["effect_999"] = new PocoEffect { InternalName = "effect_999", Name = "" } },
        };

        var vm = BuildVm(refData);

        vm.AllEffects.Single().DisplayName.Should().Be("effect_999");
    }

    [Fact]
    public void EffectListRow_SurfacesKeywordsAsCONTAINSQueryableValues()
    {
        var refData = new StubReferenceData
        {
            EffectsByKey =
            {
                ["effect_1"] = new PocoEffect { InternalName = "effect_1", Name = "Buff", Keywords = ["Buff", "Allied"] },
            },
        };

        var vm = BuildVm(refData);

        vm.AllEffects.Single().Keywords.Select(k => k.Tag).Should().BeEquivalentTo("Buff", "Allied");
    }

    [Fact]
    public void SchemaSnapshot_IncludesKeywordsColumn_SoQueryCompletionCanSuggestIt()
    {
        EffectsTabViewModel.SchemaSnapshot.Select(c => c.Name).Should().Contain("Keywords");
    }

    [Fact]
    public void Selection_RebuildsDetailViewModel_OnRowSelect()
    {
        var refData = new StubReferenceData
        {
            EffectsByKey =
            {
                ["effect_1"] = new PocoEffect { InternalName = "effect_1", Name = "Sticky!", IconId = 42, Keywords = ["Sticky"] },
            },
        };
        var vm = BuildVm(refData);

        vm.SelectedRow = vm.AllEffects.Single();

        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.DisplayName.Should().Be("Sticky!");
        vm.DetailViewModel.IconId.Should().Be(42);
    }

    [Fact]
    public void DetailViewModel_KeywordChips_AnchorOnEffectKeywordKind_AndAreNavigableWhenTargetRegistered()
    {
        var refData = new StubReferenceData
        {
            EffectsByKey =
            {
                ["effect_1"] = new PocoEffect { InternalName = "effect_1", Name = "X", Keywords = ["FrostShard"] },
            },
        };
        var vm = new EffectsTabViewModel(
            refData,
            NavFactory.WithKinds(EntityKind.EffectKeyword),
            new ReferenceDataEntityNameResolver(refData),
            new SilmarillionSettings());

        vm.SelectedRow = vm.AllEffects.Single();
        var chips = vm.DetailViewModel!.KeywordChips;

        chips.Should().ContainSingle();
        chips[0].DisplayName.Should().Be("FrostShard");
        chips[0].Reference.Kind.Should().Be(EntityKind.EffectKeyword);
        chips[0].IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void DetailViewModel_StackingType_RendersAsMetadataStripChip_WithPeerCountSuffix()
    {
        // Per #259's keyword-collapse precedent: a stacking group like "Food" (~326 effects)
        // would produce an unscannable chip wall. Render a single chip in the metadata strip
        // (folded in from the original standalone "Stacks with" section) whose label includes
        // the peer count, and whose click filters the Effects tab.
        var sticky = new PocoEffect { InternalName = "effect_1", Name = "Sticky 1", IconId = 1, StackingType = "Sticky" };
        var sticky2 = new PocoEffect { InternalName = "effect_2", Name = "Sticky 2", IconId = 2, StackingType = "Sticky" };
        var sticky3 = new PocoEffect { InternalName = "effect_3", Name = "Sticky 3", IconId = 3, StackingType = "Sticky" };
        var refData = new StubReferenceData
        {
            EffectsByKey =
            {
                ["effect_1"] = sticky,
                ["effect_2"] = sticky2,
                ["effect_3"] = sticky3,
            },
            EffectsByStackingTypeMap =
            {
                ["Sticky"] = new[] { sticky, sticky2, sticky3 },
            },
        };
        var vm = new EffectsTabViewModel(
            refData,
            NavFactory.WithKinds(EntityKind.EffectByStackingType),
            new ReferenceDataEntityNameResolver(refData),
            new SilmarillionSettings());

        vm.SelectedRow = vm.AllEffects.First(r => r.EnvelopeKey == "effect_1");
        var chip = vm.DetailViewModel!.StackingTypeChip;

        chip.Should().NotBeNull();
        chip!.DisplayName.Should().Be("Sticky (2)", because: "the chip carries the StackingType plus a peer-count suffix");
        chip.Reference.Kind.Should().Be(EntityKind.EffectByStackingType);
        chip.Reference.InternalName.Should().Be("Sticky");
        chip.IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void DetailViewModel_StackingType_NullWhenNoStackingType()
    {
        var refData = new StubReferenceData
        {
            EffectsByKey = { ["effect_1"] = new PocoEffect { InternalName = "effect_1", Name = "Loner" } },
        };
        var vm = BuildVm(refData);

        vm.SelectedRow = vm.AllEffects.Single();
        vm.DetailViewModel!.StackingTypeChip.Should().BeNull();
    }

    [Fact]
    public void DetailViewModel_StackingType_NullWhenSoleMemberOfGroup()
    {
        var lone = new PocoEffect { InternalName = "effect_1", Name = "Solo", StackingType = "Unique" };
        var refData = new StubReferenceData
        {
            EffectsByKey = { ["effect_1"] = lone },
            EffectsByStackingTypeMap = { ["Unique"] = new[] { lone } },
        };
        var vm = BuildVm(refData);

        vm.SelectedRow = vm.AllEffects.Single();
        vm.DetailViewModel!.StackingTypeChip.Should().BeNull(because: "no peers means no useful filter target");
    }

    [Fact]
    public void DetailViewModel_RequiredByAbilities_PopupMembership_EqualsIndexCollection_AndCountIsDistinct()
    {
        // #318 Gate C — the invariant. The popup is a view over the index; its membership
        // == the index members, and "View all N" == DISTINCT members.
        var effect = new PocoEffect { InternalName = "effect_1", Name = "X", IconId = 1, Keywords = ["FrostShard"] };
        var abilities = Enumerable.Range(1, 15)
            .Select(i => new Ability { InternalName = $"Strike{i}", Name = $"Strike {i}", Skill = "Sword", Level = i, IconID = 100 + i })
            .ToList();
        var refData = new StubReferenceData
        {
            EffectsByKey = { ["effect_1"] = effect },
            AbilitiesByEffectKeywordMap = { ["FrostShard"] = abilities },
        };
        var settings = new SilmarillionSettings { RequiredByAbilitiesChipCap = 12 };
        var vm = new EffectsTabViewModel(
            refData,
            NavFactory.WithKinds(EntityKind.Ability),
            new ReferenceDataEntityNameResolver(refData),
            settings);

        vm.SelectedRow = vm.AllEffects.Single();
        var detail = vm.DetailViewModel!;

        // Capped chip cluster unchanged.
        detail.RequiredByAbilityChips.Should().HaveCount(12);

        // The popup carries the *full* set (not the cap), == the index membership.
        detail.RequiredByAbilitiesPopup.Should().NotBeNull();
        var popup = detail.RequiredByAbilitiesPopup!;
        popup.TotalCount.Should().Be(15, because: "View all N == distinct index members");
        detail.RequiredByAbilitiesTotal.Should().Be(15);

        var popupMembers = popup.IsFlat
            ? popup.FlatChips.Select(c => c.Reference.InternalName)
            : popup.Sections.SelectMany(s => s.Chips).Select(c => c.Reference.InternalName).Distinct();
        popupMembers.Should().BeEquivalentTo(abilities.Select(a => a.InternalName));

        // Single reason (the stub tags everything Requires) ⇒ flat list, no section chrome.
        popup.IsFlat.Should().BeTrue(because: "one trivial reason is noise — collapse to flat (#318 Discipline)");
        popup.FlatChips.Should().HaveCount(15);
    }

    [Fact]
    public void DetailViewModel_RequiredByAbilities_PopupAlwaysBuilt_EvenWhenCountWithinCap()
    {
        var effect = new PocoEffect { InternalName = "effect_1", Name = "X", IconId = 1, Keywords = ["FrostShard"] };
        var refData = new StubReferenceData
        {
            EffectsByKey = { ["effect_1"] = effect },
            AbilitiesByEffectKeywordMap =
            {
                ["FrostShard"] = new[]
                {
                    new Ability { InternalName = "Strike", Name = "Strike", Skill = "Sword", Level = 1 },
                },
            },
        };
        var vm = BuildVm(refData);

        vm.SelectedRow = vm.AllEffects.Single();
        var detail = vm.DetailViewModel!;

        detail.RequiredByAbilityChips.Should().ContainSingle();
        detail.RequiredByAbilitiesPopup.Should().NotBeNull(
            because: "the View-all affordance is always shown, even when every ability fits as a chip");
        detail.RequiredByAbilitiesTotal.Should().Be(1);
        detail.ShowRequiredByAbilitiesPopupCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void DetailViewModel_RequiredByAbilities_NonPrimaryFieldOnlyTag_AppearsInPopup_WithCorrectProvenance()
    {
        // #318 Gate C — the load-bearing regression. This is the BloodMist case lifted to
        // the popup/VM level: a tag carried ONLY by a non-primary field (EnabledBy) must
        // still appear in the popup, with NO Requires section. The pre-#318 synthetic
        // deep-link queried EffectKeywordReqs only, so such a tag opened an empty list.
        var effect = new PocoEffect { InternalName = "effect_bm", Name = "Blood Mist", IconId = 1, Keywords = ["BloodMist"] };
        var enabledOnly = Enumerable.Range(1, 9)
            .Select(i => new Ability { InternalName = $"BloodMist{i}", Name = $"Blood Mist {i}", Skill = "Necromancy", Level = i })
            .ToList();
        var refData = new StubReferenceData
        {
            EffectsByKey = { ["effect_bm"] = effect },
            AbilitiesByEffectMatchMap =
            {
                ["BloodMist"] = enabledOnly
                    .Select(a => new EffectAbilityMatch(a, EffectAbilityMatchReason.EnabledBy))
                    .ToList(),
            },
        };
        var vm = new EffectsTabViewModel(
            refData,
            NavFactory.WithKinds(EntityKind.Ability),
            new ReferenceDataEntityNameResolver(refData),
            new SilmarillionSettings());

        vm.SelectedRow = vm.AllEffects.Single();
        var popup = vm.DetailViewModel!.RequiredByAbilitiesPopup;

        popup.Should().NotBeNull(because: "an EnabledBy-only tag still relates 9 abilities");
        popup!.TotalCount.Should().Be(9);
        popup.IsFlat.Should().BeTrue(because: "one reason ⇒ flat (a section header would be noise)");
        popup.FlatChips.Select(c => c.Reference.InternalName).Should().BeEquivalentTo(
            enabledOnly.Select(a => a.InternalName));
        // The lone section is the EnabledBy one (the VM still carries it for the count
        // derivation), but IsFlat drives the popup to render FlatChips with no section
        // chrome — and crucially there is NO Requires section. The pre-#318 bug would have
        // produced an *empty* set here (the deep-link queried EffectKeywordReqs only).
        popup.Sections.Should().ContainSingle().Which.Label.Should().Be("Enabled by");
        popup.Sections.Should().NotContain(s => s.Label == "Requires",
            because: "no ability requires this tag — the divergence the original bug produced");
    }

    [Fact]
    public void DetailViewModel_RequiredByAbilities_MultiReason_SectionsByReason_MemberCountedOnce()
    {
        // A tag whose abilities span more than one reason ⇒ sectioned popup (Requires /
        // Enabled by / Targets) in canonical order. A member qualifying for two reasons
        // appears in both sections but is counted ONCE in TotalCount.
        var effect = new PocoEffect { InternalName = "effect_1", Name = "Frost", IconId = 1, Keywords = ["FrostShard"] };
        var req = new Ability { InternalName = "Req", Name = "Req", Skill = "Ice", Level = 1 };
        var both = new Ability { InternalName = "Both", Name = "Both", Skill = "Ice", Level = 2 };
        var tgt = new Ability { InternalName = "Tgt", Name = "Tgt", Skill = "Ice", Level = 3 };
        var refData = new StubReferenceData
        {
            EffectsByKey = { ["effect_1"] = effect },
            AbilitiesByEffectMatchMap =
            {
                ["FrostShard"] = new[]
                {
                    new EffectAbilityMatch(req, EffectAbilityMatchReason.Requires),
                    new EffectAbilityMatch(both, EffectAbilityMatchReason.Requires | EffectAbilityMatchReason.Targets),
                    new EffectAbilityMatch(tgt, EffectAbilityMatchReason.Targets),
                },
            },
        };
        var vm = new EffectsTabViewModel(
            refData,
            NavFactory.WithKinds(EntityKind.Ability),
            new ReferenceDataEntityNameResolver(refData),
            new SilmarillionSettings());

        vm.SelectedRow = vm.AllEffects.Single();
        var popup = vm.DetailViewModel!.RequiredByAbilitiesPopup!;

        popup.IsFlat.Should().BeFalse(because: "two reasons present ⇒ keep section chrome");
        popup.Sections.Select(s => s.Label).Should().Equal("Requires", "Targets");
        popup.Sections.Single(s => s.Label == "Requires").Chips
            .Select(c => c.Reference.InternalName).Should().BeEquivalentTo("Req", "Both");
        popup.Sections.Single(s => s.Label == "Targets").Chips
            .Select(c => c.Reference.InternalName).Should().BeEquivalentTo("Both", "Tgt");
        popup.TotalCount.Should().Be(3, because: "a multi-reason member is counted once");
    }

    [Fact]
    public void DetailViewModel_RequiredByAbilities_OpeningPopup_DoesNotTouchNavigator_NoHistoryPushed()
    {
        // #318 — opening the popup must NOT push navigator back/forward history (the #229
        // non-navigating contract, mirroring TryOpenInWindow). The opener is swapped for a
        // capturing no-op so no window spawns; assert navigator state is pristine.
        var effect = new PocoEffect { InternalName = "effect_1", Name = "X", IconId = 1, Keywords = ["FrostShard"] };
        var refData = new StubReferenceData
        {
            EffectsByKey = { ["effect_1"] = effect },
            AbilitiesByEffectKeywordMap =
            {
                ["FrostShard"] = new[] { new Ability { InternalName = "Strike", Name = "Strike", Skill = "Sword", Level = 1 } },
            },
        };
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new EffectsTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData), new SilmarillionSettings());

        var prior = EffectDetailViewModel.ProvenancePopupOpener;
        ProvenancePopupViewModel? captured = null;
        EffectDetailViewModel.ProvenancePopupOpener = (popupVm, _) => captured = popupVm;
        try
        {
            vm.SelectedRow = vm.AllEffects.Single();
            var detail = vm.DetailViewModel!;

            nav.Current.Should().BeNull();
            nav.CanGoBack.Should().BeFalse();
            nav.CanGoForward.Should().BeFalse();

            detail.ShowRequiredByAbilitiesPopupCommand.Execute(null);

            captured.Should().NotBeNull(because: "the command invoked the opener with the built popup VM");
            // The defining assertion: opening the popup pushed no navigator state.
            nav.Current.Should().BeNull();
            nav.CanGoBack.Should().BeFalse();
            nav.CanGoForward.Should().BeFalse();
        }
        finally
        {
            EffectDetailViewModel.ProvenancePopupOpener = prior;
        }
    }

    [Fact]
    public void Duration_MapsSentinels_ToFriendlyLabels()
    {
        var refData = new StubReferenceData
        {
            EffectsByKey =
            {
                ["effect_p"] = new PocoEffect { InternalName = "effect_p", Name = "P", Duration = "Permanent" },
                ["effect_1"] = new PocoEffect { InternalName = "effect_1", Name = "A", Duration = "-1" },
                ["effect_2"] = new PocoEffect { InternalName = "effect_2", Name = "B", Duration = "-2" },
                ["effect_30"] = new PocoEffect { InternalName = "effect_30", Name = "C", Duration = "30" },
                ["effect_180"] = new PocoEffect { InternalName = "effect_180", Name = "D", Duration = "180" },
            },
        };
        var vm = BuildVm(refData);

        Label(vm, "effect_p").Should().Be("Permanent");
        Label(vm, "effect_1").Should().Be("Until cleansed");
        Label(vm, "effect_2").Should().Be("Until removed");
        Label(vm, "effect_30").Should().Be("30 seconds");
        Label(vm, "effect_180").Should().Be("3 minutes");

        static string? Label(EffectsTabViewModel vm, string envelopeKey)
        {
            vm.SelectedRow = vm.AllEffects.First(r => r.EnvelopeKey == envelopeKey);
            return vm.DetailViewModel!.DurationLabel;
        }
    }

    [Fact]
    public void DisplayMode_FiltersOutUniversalDefault()
    {
        var refData = new StubReferenceData
        {
            EffectsByKey =
            {
                ["effect_1"] = new PocoEffect { InternalName = "effect_1", Name = "A", DisplayMode = "Effect" },
                ["effect_2"] = new PocoEffect { InternalName = "effect_2", Name = "B", DisplayMode = "Cocoon" },
            },
        };
        var vm = BuildVm(refData);

        vm.SelectedRow = vm.AllEffects.First(r => r.EnvelopeKey == "effect_1");
        vm.DetailViewModel!.DisplayMode.Should().BeNull(because: "\"Effect\" is the universal default and is filtered out");

        vm.SelectedRow = vm.AllEffects.First(r => r.EnvelopeKey == "effect_2");
        vm.DetailViewModel!.DisplayMode.Should().Be("Cocoon");
    }

    [Fact]
    public void FileUpdated_EffectsRefresh_RebuildsListPreservingSelectionByEnvelopeKey()
    {
        var refData = new StubReferenceData
        {
            EffectsByKey =
            {
                ["effect_1"] = new PocoEffect { InternalName = "effect_1", Name = "Original", IconId = 1 },
            },
        };
        var vm = BuildVm(refData);
        vm.SelectedRow = vm.AllEffects.Single();

        // Mutate the underlying refData with a renamed effect at the same envelope key.
        refData.EffectsByKey["effect_1"] = new PocoEffect { InternalName = "effect_1", Name = "Renamed", IconId = 2 };
        refData.RaiseFileUpdated("effects");

        vm.AllEffects.Single().DisplayName.Should().Be("Renamed");
        vm.SelectedRow.Should().NotBeNull();
        vm.SelectedRow!.EnvelopeKey.Should().Be("effect_1");
        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.DisplayName.Should().Be("Renamed");
    }

    [Fact]
    public void FileUpdated_AbilitiesRefresh_RebuildsDetailVM_SoCrossLinkChipsRefresh()
    {
        var effect = new PocoEffect { InternalName = "effect_1", Name = "X", IconId = 1, Keywords = ["FrostShard"] };
        var refData = new StubReferenceData
        {
            EffectsByKey = { ["effect_1"] = effect },
            AbilitiesByEffectKeywordMap = { ["FrostShard"] = new[] { new Ability { InternalName = "Strike", Name = "Strike", Skill = "Sword", Level = 1 } } },
        };
        var vm = BuildVm(refData);
        vm.SelectedRow = vm.AllEffects.Single();
        vm.DetailViewModel!.RequiredByAbilityChips.Should().ContainSingle();

        refData.AbilitiesByEffectKeywordMap["FrostShard"] = new[]
        {
            new Ability { InternalName = "Strike", Name = "Strike", Skill = "Sword", Level = 1 },
            new Ability { InternalName = "Slash", Name = "Slash", Skill = "Sword", Level = 2 },
        };
        refData.RaiseFileUpdated("abilities");

        vm.DetailViewModel!.RequiredByAbilityChips.Should().HaveCount(2);
    }

    private static EffectsTabViewModel BuildVm(StubReferenceData refData) =>
        new EffectsTabViewModel(
            refData,
            new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()),
            new ReferenceDataEntityNameResolver(refData),
            new SilmarillionSettings());

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<string, PocoEffect> EffectsByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<PocoEffect>> EffectsByStackingTypeMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Ability>> AbilitiesByEffectKeywordMap { get; } = new(StringComparer.Ordinal);

        // Optional explicit-provenance override: a tag present here supersedes
        // AbilitiesByEffectKeywordMap for that tag, letting a test model EnabledBy-only /
        // multi-reason cases (#318 Gate C). Plain AbilitiesByEffectKeywordMap entries
        // default every member to Requires (sufficient for membership/count assertions).
        public Dictionary<string, IReadOnlyList<EffectAbilityMatch>> AbilitiesByEffectMatchMap { get; } = new(StringComparer.Ordinal);

        public List<AbilityDynamicDot> DotRules { get; } = new();
        public List<AbilityDynamicSpecialValue> SpecialValueRules { get; } = new();

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

        // Surface Ability via a derived AbilitiesByInternalName so the resolver can project
        // InternalName → Name; otherwise required-by-ability chip DisplayName falls back to
        // raw InternalName and tests asserting DisplayName silently regress (cookbook caveat).
        public IReadOnlyDictionary<string, Ability> Abilities { get; } = new Dictionary<string, Ability>();
        public IReadOnlyDictionary<string, Ability> AbilitiesByInternalName =>
            AbilitiesByEffectKeywordMap.Values.SelectMany(v => v)
                .Concat(AbilitiesByEffectMatchMap.Values.SelectMany(v => v.Select(m => m.Ability)))
                .GroupBy(a => a.InternalName!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        public IReadOnlyDictionary<string, PocoEffect> Effects => EffectsByKey;
        public IReadOnlyDictionary<string, PocoEffect> EffectsByInternalName => EffectsByKey;
        public IReadOnlyDictionary<string, IReadOnlyList<PocoEffect>> EffectsByStackingType => EffectsByStackingTypeMap;
        // Plain map entries default every member to Requires (enough for membership/count
        // assertions). AbilitiesByEffectMatchMap entries carry explicit reasons and
        // supersede the plain map for that tag — used by the #318 Gate-C provenance tests
        // (EnabledBy-only, multi-reason). The real provenance regression is also asserted
        // against bundled data in ReferenceDataServiceEffectCrossLinkIndexTests.
        public IReadOnlyDictionary<string, IReadOnlyList<EffectAbilityMatch>> AbilitiesByEffectKeyword
        {
            get
            {
                var result = new Dictionary<string, IReadOnlyList<EffectAbilityMatch>>(StringComparer.Ordinal);
                foreach (var kv in AbilitiesByEffectKeywordMap)
                    result[kv.Key] = kv.Value
                        .Select(a => new EffectAbilityMatch(a, EffectAbilityMatchReason.Requires))
                        .ToList();
                foreach (var kv in AbilitiesByEffectMatchMap)
                    result[kv.Key] = kv.Value;
                return result;
            }
        }
        public IReadOnlyList<AbilityDynamicDot> AbilityDynamicDots => DotRules;
        public IReadOnlyList<AbilityDynamicSpecialValue> AbilityDynamicSpecialValues => SpecialValueRules;

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated;
        public void RaiseFileUpdated(string fileKey) => FileUpdated?.Invoke(this, fileKey);
    }
}
