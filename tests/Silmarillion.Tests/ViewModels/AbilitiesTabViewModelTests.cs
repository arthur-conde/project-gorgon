using System.IO;
using FluentAssertions;
using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.Query;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

// Helper duplicated from NpcsTabViewModelTests via file-scope — same shape but kept local so each
// test file is self-contained (file static is duplicate-free across the assembly).
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

public sealed class AbilitiesTabViewModelTests
{
    [Fact]
    public void AllAbilities_PopulatedFromReferenceData_OrderedByName()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability { InternalName = "SwordSlash", Name = "Sword Slash", Skill = "Sword", Level = 1 },
                ["ability_2"] = new Ability { InternalName = "AnimalBite", Name = "Animal Bite", Skill = "Unarmed", Level = 1 },
            },
        };

        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.AllAbilities.Should().HaveCount(2);
        vm.AllAbilities.Select(a => a.Name).Should().Equal("Animal Bite", "Sword Slash");
        vm.AllAbilities[0].InternalName.Should().Be("AnimalBite");
    }

    [Fact]
    public void AbilityListRow_SkillResolvedThroughSkillsDictionary()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability { InternalName = "Mentalism5", Name = "Mental Blast 5", Skill = "Mentalism", Level = 5 },
            },
            SkillsMap =
            {
                ["Mentalism"] = new SkillEntry("Mentalism", DisplayName: "Mind Magic", Id: 0, Combat: true, XpTable: "", MaxBonusLevels: 0, Parents: [], Rewards: new Dictionary<string, SkillRewardEntry>()),
            },
        };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.AllAbilities.Single().Skill.Should().Be("Mind Magic");
    }

    [Fact]
    public void AbilityListRow_FallsBackToRawSkillKey_WhenNoSkillEntry()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability { InternalName = "Atk1", Name = "Atk", Skill = "Mystery", Level = 1 },
            },
        };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.AllAbilities.Single().Skill.Should().Be("Mystery");
    }

    [Fact]
    public void AbilityListRow_SkillIsUnknown_WhenAbilitySkillNull()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability { InternalName = "FreeAbility", Name = "Free", Skill = null, Level = 1 },
            },
        };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.AllAbilities.Single().Skill.Should().Be("(unknown)");
    }

    [Fact]
    public void AbilityListRow_SkillLevelDisplay_CombinesSkillAndLevel()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability { InternalName = "SwordSlash", Name = "Sword Slash", Skill = "Sword", Level = 7 },
                ["ability_2"] = new Ability { InternalName = "Mystery", Name = "Mystery", Skill = null, Level = 3 },
            },
        };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.AllAbilities.Single(r => r.InternalName == "SwordSlash").SkillLevelDisplay.Should().Be("Sword 7");
        // Null-skill fallback chains through AbilityListRow.Skill's "(unknown)" projection.
        vm.AllAbilities.Single(r => r.InternalName == "Mystery").SkillLevelDisplay.Should().Be("(unknown) 3");
    }

    [Fact]
    public void DetailViewModel_SkillLevelDisplay_FallsBackToLevelOnly_WhenSkillMissing()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability { InternalName = "SwordSlash", Name = "Sword Slash", Skill = "Sword", Level = 7 },
                ["ability_2"] = new Ability { InternalName = "Mystery", Name = "Mystery", Skill = null, Level = 3 },
            },
        };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single(r => r.InternalName == "SwordSlash");
        vm.DetailViewModel!.SkillLevelDisplay.Should().Be("Sword 7");

        vm.SelectedRow = vm.AllAbilities.Single(r => r.InternalName == "Mystery");
        // Detail VM keeps SkillDisplayName nullable (unlike the list row's "(unknown)" projection)
        // so the combined chip falls back to "Level N" rather than "(unknown) N".
        vm.DetailViewModel!.SkillLevelDisplay.Should().Be("Level 3");
    }

    [Fact]
    public void AbilityListRow_NameFallsBackToInternalName_WhenPocoNameMissing()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability { InternalName = "Internal42", Name = null, Skill = "Sword", Level = 1 },
            },
        };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.AllAbilities.Single().Name.Should().Be("Internal42");
    }

    [Fact]
    public void QueryText_KeywordsContains_FiltersToMatchingAbility()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability
                {
                    InternalName = "AttackOne",
                    Name = "Attack One",
                    Skill = "Sword",
                    Level = 1,
                    Keywords = ["Attack", "Melee"],
                },
                ["ability_2"] = new Ability
                {
                    InternalName = "BuffOne",
                    Name = "Buff One",
                    Skill = "Druid",
                    Level = 1,
                    Keywords = ["Buff"],
                },
            },
        };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        const string queryString = "Keywords CONTAINS \"Attack\"";
        var columns = ColumnBindingHelper.BuildFromProperties(typeof(AbilityListRow));
        var predicate = QueryCompiler.Compile(queryString, columns);
        predicate.Should().NotBeNull();

        var matches = vm.AllAbilities.Where(row => predicate!(row)).ToList();

        matches.Should().ContainSingle(r => r.InternalName == "AttackOne");
        matches.Should().NotContain(r => r.InternalName == "BuffOne");
    }

    [Fact]
    public void SelectingRow_BuildsDetailViewModel()
    {
        var ability = new Ability { InternalName = "SwordSlash", Name = "Sword Slash", Skill = "Sword", Level = 1 };
        var refData = new StubReferenceData
        {
            AbilitiesByKey = { ["ability_1"] = ability },
        };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();

        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.Ability.Should().Be(ability);
        vm.DetailViewModel.InternalName.Should().Be("SwordSlash");
    }

    [Fact]
    public void DeselectingRow_ClearsDetailViewModel()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey = { ["ability_1"] = new Ability { InternalName = "Atk1", Name = "Attack", Skill = "Sword", Level = 1 } },
        };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        vm.SelectedRow = null;

        vm.DetailViewModel.Should().BeNull();
    }

    [Fact]
    public void DetailViewModel_PrerequisiteChip_NavigableWhenAbilityKindRegistered()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability { InternalName = "SwordSlash", Name = "Sword Slash", Skill = "Sword", Level = 1 },
                ["ability_2"] = new Ability { InternalName = "SwordSlash2", Name = "Sword Slash 2", Skill = "Sword", Level = 12, Prerequisite = "SwordSlash" },
            },
        };
        var vm = new AbilitiesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Ability), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single(r => r.InternalName == "SwordSlash2");

        var chip = vm.DetailViewModel!.PrerequisiteChip;
        chip.Should().NotBeNull();
        chip!.Reference.Should().Be(EntityRef.Ability("SwordSlash"));
        chip.DisplayName.Should().Be("Sword Slash");
        chip.IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void DetailViewModel_CoreMechanics_ProjectsTargetResetTimeCosts()
    {
        var ability = new Ability
        {
            InternalName = "Slash",
            Name = "Slash",
            Skill = "Sword",
            Level = 1,
            Target = "Enemy",
            ResetTime = 4.5,
            Costs = [new AbilityCost { Currency = "Gold", Price = 100 }],
        };
        var refData = new StubReferenceData { AbilitiesByKey = { ["ability_1"] = ability } };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        var detail = vm.DetailViewModel!;

        detail.Target.Should().Be("Enemy");
        detail.ResetTime.Should().Be(4.5);
        detail.CostRows.Should().ContainSingle();
        detail.CostRows[0].Currency.Should().Be("Gold");
        detail.CostRows[0].Price.Should().Be(100);
    }

    [Fact]
    public void DetailViewModel_Prerequisites_ProjectsItemKeywordReqsAsChips()
    {
        var ability = new Ability
        {
            InternalName = "SwordSlash",
            Name = "Sword Slash",
            Skill = "Sword",
            Level = 1,
            ItemKeywordReqs = ["Sword"],
            EffectKeywordReqs = ["BattleRage"],
        };
        var refData = new StubReferenceData { AbilitiesByKey = { ["ability_1"] = ability } };
        var vm = new AbilitiesTabViewModel(refData, NavFactory.WithKinds(EntityKind.EffectKeyword, EntityKind.ItemByKeyword), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        var detail = vm.DetailViewModel!;

        // #327: restored the single-keyword Items filter pivot via EntityKind.ItemByKeyword
        // (the symmetric Items-side twin of EffectKeyword). #326 had degraded this chip to
        // non-navigable plain text when the double-duty ItemKeyword kind was retired for
        // its fan-out use.
        detail.ItemKeywordReqChips.Should().ContainSingle();
        var chip = detail.ItemKeywordReqChips[0];
        chip.DisplayName.Should().Be("Sword");
        chip.Reference.Kind.Should().Be(EntityKind.ItemByKeyword);
        chip.Reference.InternalName.Should().Be("Sword");
        chip.IsNavigable.Should().BeTrue();

        detail.EffectKeywordReqChips.Should().ContainSingle();
        var effectChip = detail.EffectKeywordReqChips[0];
        effectChip.DisplayName.Should().Be("BattleRage");
        effectChip.Reference.Kind.Should().Be(EntityKind.EffectKeyword);
        effectChip.Reference.InternalName.Should().Be("BattleRage");
        effectChip.IsNavigable.Should().BeTrue();

        detail.FormGateLabels.Should().BeEmpty();
    }

    [Theory]
    [InlineData("form:Cow", "Must be in Cow Form")]
    [InlineData("form:Fox", "Must be in Fox Form")]
    [InlineData("Werewolf", "Must be in Wolf Form")]
    [InlineData("Beast", "Must be in a beast form")]
    public void DetailViewModel_FormGateInItemKeywordReqs_SurfacesAsFormGateLabel(
        string gateValue, string errorMessage)
    {
        var ability = new Ability
        {
            InternalName = "GatedAbility",
            Name = "Gated Ability",
            Skill = "Werewolf",
            Level = 1,
            ItemKeywordReqs = [gateValue],
            ItemKeywordReqErrorMessage = errorMessage,
        };
        var refData = new StubReferenceData { AbilitiesByKey = { ["ability_1"] = ability } };
        var vm = new AbilitiesTabViewModel(refData, NavFactory.WithKinds(), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        var detail = vm.DetailViewModel!;

        detail.ItemKeywordReqChips.Should().BeEmpty();
        detail.FormGateLabels.Should().ContainSingle().Which.Should().Be(errorMessage);
    }

    [Fact]
    public void DetailViewModel_FormGateWithoutErrorMessage_DerivesLabelFromGateValue()
    {
        var ability = new Ability
        {
            InternalName = "GatedAbility",
            Name = "Gated Ability",
            Skill = "Cow",
            Level = 1,
            ItemKeywordReqs = ["form:Cow", "Werewolf"],
            ItemKeywordReqErrorMessage = null,
        };
        var refData = new StubReferenceData { AbilitiesByKey = { ["ability_1"] = ability } };
        var vm = new AbilitiesTabViewModel(refData, NavFactory.WithKinds(), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        var detail = vm.DetailViewModel!;

        detail.ItemKeywordReqChips.Should().BeEmpty();
        detail.FormGateLabels.Should().Equal("Cow Form", "Werewolf Form");
    }

    [Fact]
    public void DetailViewModel_NoFormGate_FormGateLabelsEmpty()
    {
        var ability = new Ability
        {
            InternalName = "PlainAbility",
            Name = "Plain Ability",
            Skill = "Sword",
            Level = 1,
            ItemKeywordReqs = ["Sword"],
        };
        var refData = new StubReferenceData { AbilitiesByKey = { ["ability_1"] = ability } };
        var vm = new AbilitiesTabViewModel(refData, NavFactory.WithKinds(), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        var detail = vm.DetailViewModel!;

        detail.FormGateLabels.Should().BeEmpty();
        detail.ItemKeywordReqChips.Should().ContainSingle()
            .Which.DisplayName.Should().Be("Sword");
    }

    [Fact]
    public void DetailViewModel_ConditionalKeywords_ProjectsRowsWithLabels()
    {
        var ability = new Ability
        {
            InternalName = "ManyCuts",
            Name = "Many Cuts",
            Skill = "Sword",
            Level = 3,
            ConditionalKeywords = new[]
            {
                new AbilityConditionalKeyword { Default = true, EffectKeywordMustNotExist = "ManyCutsAoE", Keyword = "Melee" },
                new AbilityConditionalKeyword { EffectKeywordMustExist = "ManyCutsAoE", Keyword = "Burst" },
            },
        };
        var refData = new StubReferenceData { AbilitiesByKey = { ["ability_1"] = ability } };
        var vm = new AbilitiesTabViewModel(refData, NavFactory.WithKinds(EntityKind.EffectKeyword), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        var rows = vm.DetailViewModel!.ConditionalKeywordRows;

        rows.Should().HaveCount(2);
        rows[0].Keyword.Should().Be("Melee");
        rows[0].Condition.Should().Be("Default");
        rows[0].EffectKeywordChip.Should().BeNull();
        rows[1].Keyword.Should().Be("Burst");
        rows[1].Condition.Should().Be("When effect keyword present:");
        var burstChip = rows[1].EffectKeywordChip;
        burstChip.Should().NotBeNull();
        burstChip!.DisplayName.Should().Be("ManyCutsAoE");
        burstChip.Reference.Kind.Should().Be(EntityKind.EffectKeyword);
        burstChip.Reference.InternalName.Should().Be("ManyCutsAoE");
        burstChip.IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void DetailViewModel_AmmoSection_SingleKeyword_FoldsAmmoDescriptionIntoChipLabel()
    {
        // Single-keyword ammo abilities (96% of the corpus) get a friendly chip label sourced
        // from AmmoDescription, and the standalone AmmoDescription line is suppressed to avoid
        // duplicating what's already in the chip (and usually in Description prose too).
        var ability = new Ability
        {
            InternalName = "FireArrow",
            Name = "Fire Arrow",
            Description = "Fire a flaming arrow. Uses a Beginner's Arrow.",
            Skill = "Archery",
            Level = 5,
            AmmoDescription = "Beginner's Arrow",
            AmmoKeywords = [new AbilityAmmoKeyword { ItemKeyword = "Arrow1", Count = 1 }],
            AmmoStickChance = 0.5,
            AmmoConsumeChance = 1.0,
        };
        var refData = new StubReferenceData { AbilitiesByKey = { ["ability_1"] = ability } };
        var vm = new AbilitiesTabViewModel(refData, NavFactory.WithKinds(EntityKind.ItemByKeyword), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        var detail = vm.DetailViewModel!;

        detail.HasAmmoSection.Should().BeTrue();
        detail.AmmoDescription.Should().Be("Beginner's Arrow");
        detail.ShowAmmoDescription.Should().BeFalse(because: "single-keyword case folds AmmoDescription into the chip label");
        detail.AmmoKeywordRows.Should().ContainSingle();
        var row = detail.AmmoKeywordRows[0];
        row.Chip.DisplayName.Should().Be("Beginner's Arrow", because: "AmmoDescription is the friendly label for the keyword");
        // #327: ammo-keyword chip is a single-keyword Items filter pivot, restored via
        // EntityKind.ItemByKeyword. The label folds in AmmoDescription, but the deep-link
        // payload carries the raw keyword tag so the Items filter is Keywords CONTAINS.
        row.Chip.IsNavigable.Should().BeTrue();
        row.Chip.Reference.Kind.Should().Be(EntityKind.ItemByKeyword);
        row.Chip.Reference.InternalName.Should().Be("Arrow1");
        row.Count.Should().Be(1);
        detail.AmmoStickChance.Should().Be(0.5);
        detail.AmmoConsumeChance.Should().Be(1.0);
    }

    [Fact]
    public void DetailViewModel_AmmoSection_MultiKeyword_KeepsAmmoDescriptionAndPerKeywordChips()
    {
        // Multi-keyword ammo abilities (~48 cases like HamstringThrow) carry OR-substitution
        // context in AmmoDescription ("Simple Throwing Knife (or Crystal Ice x2)") that no
        // single chip can convey. The standalone line stays; each chip keeps its raw ItemKeyword.
        var ability = new Ability
        {
            InternalName = "HamstringThrow1",
            Name = "Hamstring Throw",
            Description = "A thrown blade. Consumes 1 Simple Throwing Knife.",
            Skill = "Knife",
            Level = 5,
            AmmoDescription = "Simple Throwing Knife (or Crystal Ice x2)",
            AmmoKeywords =
            [
                new AbilityAmmoKeyword { ItemKeyword = "ThrowingKnife1", Count = 1 },
                new AbilityAmmoKeyword { ItemKeyword = "CrystalIce", Count = 2 },
            ],
        };
        var refData = new StubReferenceData { AbilitiesByKey = { ["ability_1"] = ability } };
        var vm = new AbilitiesTabViewModel(refData, NavFactory.WithKinds(EntityKind.ItemByKeyword), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        var detail = vm.DetailViewModel!;

        detail.ShowAmmoDescription.Should().BeTrue(because: "multi-keyword case keeps the line — AmmoDescription carries OR-substitution context the chips alone can't convey");
        detail.AmmoKeywordRows.Select(r => r.Chip.DisplayName).Should().Equal("ThrowingKnife1", "CrystalIce");
        // #327: each ammo keyword is a single-keyword Items filter pivot, restored via
        // EntityKind.ItemByKeyword (each chip is its own 1:1 pivot — multi-keyword here
        // means multiple independent chips, NOT a fan-out / composite slot).
        detail.AmmoKeywordRows.Should().OnlyContain(r => r.Chip.IsNavigable);
        detail.AmmoKeywordRows.Select(r => r.Chip.Reference.Kind)
            .Should().AllBeEquivalentTo(EntityKind.ItemByKeyword);
        detail.AmmoKeywordRows.Select(r => r.Chip.Reference.InternalName)
            .Should().Equal("ThrowingKnife1", "CrystalIce");
    }

    [Fact]
    public void DetailViewModel_AmmoSection_MultiKeyword_SuppressesAmmoDescriptionWhenContainedInDescription()
    {
        // Belt-and-braces edge case: if a multi-keyword ability's AmmoDescription happens to be
        // a substring of Description, drop the standalone line — chips carry the actionable bits.
        var ability = new Ability
        {
            InternalName = "MultiAmmo",
            Name = "Multi Ammo",
            Description = "Uses ammo Combined Pack",
            Skill = "Test",
            Level = 1,
            AmmoDescription = "Combined Pack",
            AmmoKeywords =
            [
                new AbilityAmmoKeyword { ItemKeyword = "Ammo1", Count = 1 },
                new AbilityAmmoKeyword { ItemKeyword = "Ammo2", Count = 1 },
            ],
        };
        var refData = new StubReferenceData { AbilitiesByKey = { ["ability_1"] = ability } };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        var detail = vm.DetailViewModel!;

        detail.ShowAmmoDescription.Should().BeFalse();
    }

    [Fact]
    public void DetailViewModel_EnvironmentalFlags_OnlyNotableValuesSurface()
    {
        // Default-value noise filtering: WorksUnderwater is rare (most abilities don't), so
        // setting it true should produce a row. WorksInCombat=true is the common case, so it
        // should NOT produce a row. Only `WorksInCombat = false` flags the rare in-combat ban.
        var ability = new Ability
        {
            InternalName = "WeirdAbility",
            Name = "Weird",
            Skill = "Sword",
            Level = 1,
            WorksUnderwater = true,
            WorksInCombat = false,
        };
        var refData = new StubReferenceData { AbilitiesByKey = { ["ability_1"] = ability } };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        var flags = vm.DetailViewModel!.EnvironmentalFlags;

        flags.Should().Contain(f => f.Label == "Works underwater");
        flags.Should().Contain(f => f.Label == "Cannot be used in combat");
    }

    [Fact]
    public void DetailViewModel_EnvironmentalFlags_EmptyWhenAllDefault()
    {
        var ability = new Ability { InternalName = "DefaultAbility", Name = "Default", Skill = "Sword", Level = 1 };
        var refData = new StubReferenceData { AbilitiesByKey = { ["ability_1"] = ability } };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        vm.DetailViewModel!.EnvironmentalFlags.Should().BeEmpty();
    }

    [Fact]
    public void DetailViewModel_PvEStats_ProjectsDamageRangeAndPowerCost()
    {
        var ability = new Ability
        {
            InternalName = "Strike",
            Name = "Strike",
            Skill = "Sword",
            Level = 1,
            PvE = new AbilityPvE
            {
                Damage = 25,
                Range = 5,
                PowerCost = 10,
                DoTs = [new AbilityDoT { DamagePerTick = 2, DamageType = "Trauma", NumTicks = 6, Duration = 6 }],
                SpecialValues = [new AbilitySpecialValue { Label = "Heal", Value = 15, Suffix = "HP" }],
            },
        };
        var refData = new StubReferenceData { AbilitiesByKey = { ["ability_1"] = ability } };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        var detail = vm.DetailViewModel!;

        detail.PvEStats.Should().Contain(f => f.Label == "Damage" && f.Value == "25");
        detail.PvEStats.Should().Contain(f => f.Label == "Range" && f.Value == "5");
        detail.PvEStats.Should().Contain(f => f.Label == "Power cost" && f.Value == "10");
        detail.DoTRows.Should().ContainSingle();
        detail.DoTRows[0].DamagePerTick.Should().Be(2);
        detail.SpecialValueRows.Should().ContainSingle();
        detail.SpecialValueRows[0].Label.Should().Be("Heal");
    }

    [Fact]
    public void DetailViewModel_AbilitiesInGroup_ProjectsSiblingsExcludingSelf()
    {
        var a1 = new Ability { InternalName = "GroupA1", Name = "A1", Skill = "Sword", Level = 1, AbilityGroup = "GroupA", IconID = 1 };
        var a2 = new Ability { InternalName = "GroupA2", Name = "A2", Skill = "Sword", Level = 2, AbilityGroup = "GroupA", IconID = 2 };
        var a3 = new Ability { InternalName = "GroupA3", Name = "A3", Skill = "Sword", Level = 3, AbilityGroup = "GroupA", IconID = 3 };
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = a1,
                ["ability_2"] = a2,
                ["ability_3"] = a3,
            },
            AbilitiesInGroupMap =
            {
                ["GroupA"] = new[] { a1, a2, a3 },
            },
        };
        var vm = new AbilitiesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Ability), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single(r => r.InternalName == "GroupA2");
        var chips = vm.DetailViewModel!.AbilitiesInGroupChips;

        chips.Should().HaveCount(2);
        chips.Select(c => c.Reference.InternalName).Should().BeEquivalentTo(["GroupA1", "GroupA3"]);
        chips.Should().NotContain(c => c.Reference.InternalName == "GroupA2");
    }

    [Fact]
    public void DetailViewModel_UpgradesToChips_ProjectsReverseLookup()
    {
        var base1 = new Ability { InternalName = "SwordSlash", Name = "Sword Slash", Skill = "Sword", Level = 1 };
        var upg1 = new Ability { InternalName = "SwordSlash2", Name = "Sword Slash 2", Skill = "Sword", Level = 12, UpgradeOf = "SwordSlash" };
        var upg2 = new Ability { InternalName = "SwordSlash3", Name = "Sword Slash 3", Skill = "Sword", Level = 25, UpgradeOf = "SwordSlash" };
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = base1,
                ["ability_2"] = upg1,
                ["ability_3"] = upg2,
            },
            AbilitiesUpgradingFromMap =
            {
                ["SwordSlash"] = new[] { upg1, upg2 },
            },
        };
        var vm = new AbilitiesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Ability), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single(r => r.InternalName == "SwordSlash");
        var chips = vm.DetailViewModel!.UpgradesToChips;

        chips.Should().HaveCount(2);
        chips.Select(c => c.Reference.InternalName).Should().Equal(["SwordSlash2", "SwordSlash3"]);
    }

    [Fact]
    public void DetailViewModel_SourceChips_ProjectsTrainerNpcsFromAbilitySources()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability { InternalName = "WoundingShot", Name = "Wounding Shot", Skill = "Archery", Level = 1 },
            },
            NpcsByKey =
            {
                ["NPC_Flia"] = new Npc { Name = "Flia" },
            },
            AbilitySourcesMap =
            {
                ["WoundingShot"] = new[]
                {
                    new AbilitySource("Skill", null, null),
                    new AbilitySource("Training", "NPC_Flia", null),
                },
            },
        };
        var vm = new AbilitiesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Npc), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRow = vm.AllAbilities.Single();
        var chips = vm.DetailViewModel!.SourceChips;

        chips.Should().ContainSingle(c => c.Reference.InternalName == "NPC_Flia");
        chips[0].DisplayName.Should().Be("Flia");
        chips[0].IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void FileUpdated_AbilitiesRefresh_RebuildsAllAbilities_PreservingSelectionByInternalName()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey = { ["ability_1"] = new Ability { InternalName = "Atk1", Name = "Atk", Skill = "Sword", Level = 1 } },
        };
        var vm = new AbilitiesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new ReferenceDataEntityNameResolver(refData));
        vm.SelectedRow = vm.AllAbilities.Single();

        // Swap in a fresh Ability instance for the same internal name.
        refData.AbilitiesByKey["ability_1"] = new Ability { InternalName = "Atk1", Name = "Atk", Skill = "Sword", Level = 1, Description = "Fresh." };
        refData.RaiseFileUpdated("abilities");

        vm.SelectedRow.Should().NotBeNull();
        vm.SelectedRow!.InternalName.Should().Be("Atk1");
        vm.DetailViewModel!.Description.Should().Be("Fresh.");
    }

    [Fact]
    public void RealBundledAbility_SwordSlash_ProjectsSensibly()
    {
        // Real-data sanity walk: load the actual bundled abilities.json + supporting files and
        // verify a representative entry projects without (unknown) sentinels, with the expected
        // groups present, and with chips resolving to display names. Skips when bundled data
        // isn't co-located (test DLL run from an isolated CI shape).
        var bundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!File.Exists(Path.Combine(bundled, "abilities.json"))) return;

        var refData = BuildRealRefData(bundled);
        if (refData is null) return;

        var vm = new AbilitiesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Ability, EntityKind.Npc), new ReferenceDataEntityNameResolver(refData));

        var row = vm.AllAbilities.FirstOrDefault(r => r.InternalName == "SwordSlash2");
        row.Should().NotBeNull("SwordSlash2 is a stable ability in bundled abilities.json");
        row!.Skill.Should().Be("Sword");
        row.Skill.Should().NotBe("(unknown)");

        vm.SelectedRow = row;
        var detail = vm.DetailViewModel!;
        detail.DisplayName.Should().NotBeNullOrEmpty();
        detail.PrerequisiteChip.Should().NotBeNull(because: "SwordSlash2 lists SwordSlash as Prerequisite");
        detail.PrerequisiteChip!.DisplayName.Should().Be("Sword Slash");
        detail.PrerequisiteChip.IsNavigable.Should().BeTrue();
    }

    [Fact]
    public void RealBundledAbility_ManyCuts_ProjectsSensibly()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!File.Exists(Path.Combine(bundled, "abilities.json"))) return;

        var refData = BuildRealRefData(bundled);
        if (refData is null) return;

        var vm = new AbilitiesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Ability, EntityKind.Npc), new ReferenceDataEntityNameResolver(refData));

        var row = vm.AllAbilities.FirstOrDefault(r => r.InternalName == "ManyCuts");
        row.Should().NotBeNull("ManyCuts is a stable Sword ability in bundled abilities.json");
        row!.Skill.Should().Be("Sword");

        vm.SelectedRow = row;
        var detail = vm.DetailViewModel!;
        detail.ConditionalKeywordRows.Should().NotBeEmpty(because: "ManyCuts has a Default ConditionalKeyword (Melee)");
        detail.PvEStats.Should().NotBeEmpty();
        detail.DoTRows.Should().NotBeEmpty(because: "ManyCuts has a Trauma DoT");
    }

    // ── Phase 5 grammar-primitive projections ──────────────────────────────

    [Fact]
    public void HeaderStrip_CollapsesBadges_NoGoldByConstruction_AndFooterIsCopyableKey()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability
                {
                    InternalName = "SwordSlash3", Name = "Sword Slash 3", Skill = "Sword",
                    Level = 12, Rank = "Rank 3", AbilityGroup = "SwordSlash", AbilityGroupName = "Sword Slash",
                },
            },
        };
        var vm = new AbilitiesTabViewModel(
            refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()),
            new ReferenceDataEntityNameResolver(refData));
        vm.SelectedRow = vm.AllAbilities.Single();
        var d = vm.DetailViewModel!;

        // The skill/rank/group badge BOXES collapse into ONE inert Strip
        // (pilot StatStrip). FactTableVm has no brush, so the named G-b
        // "Ability Rank" gold cannot be expressed by construction.
        d.HeaderStrip.Layout.Should().Be(FactTableLayout.Strip);
        d.HeaderStrip.Pairs.Should().BeEquivalentTo(new[]
        {
            new FactPair(null, "Sword 12"),
            new FactPair(null, "Rank 3"),
            new FactPair("Group", "Sword Slash"),
        }, o => o.WithStrictOrdering());

        // InternalName is a cross-entity reference key ⇒ copyable KEY.
        d.Footer.Ids.Should().ContainSingle();
        d.Footer.Ids[0].LabelTag.Should().Be("KEY");
        d.Footer.Ids[0].Value.Should().Be("SwordSlash3");
        d.Footer.Ids[0].Copyable.Should().BeTrue();
    }

    [Fact]
    public void KeywordReqs_AreTagFormSetRefs_ActionableMirrorsNavigability()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability
                {
                    InternalName = "Atk", Name = "Atk", Skill = "Sword", Level = 1,
                    ItemKeywordReqs = ["Sword"], EffectKeywordReqs = ["Bleeding"],
                },
            },
        };
        // Wired: both keyword targets registered ⇒ actionable.
        var vm = new AbilitiesTabViewModel(
            refData, NavFactory.WithKinds(EntityKind.ItemByKeyword, EntityKind.EffectKeyword),
            new ReferenceDataEntityNameResolver(refData));
        vm.SelectedRow = vm.AllAbilities.Single();
        var d = vm.DetailViewModel!;

        d.ItemKeywordReqSetRefs.Should().ContainSingle();
        d.ItemKeywordReqSetRefs[0].SetRef.Label.Should().Be("Sword");
        d.ItemKeywordReqSetRefs[0].SetRef.IsSummaryForm.Should().BeFalse("keyword chips are tag-form");
        d.ItemKeywordReqSetRefs[0].SetRef.IsActionable.Should().BeTrue();
        d.ItemKeywordReqSetRefs[0].Activate.Should().NotBeNull();
        d.EffectKeywordReqSetRefs[0].SetRef.Label.Should().Be("Bleeding");
        d.EffectKeywordReqSetRefs[0].SetRef.IsActionable.Should().BeTrue();

        // Unwired ⇒ availability corollary (blue chassis, IsActionable=false, no command).
        var vmU = new AbilitiesTabViewModel(
            refData, NavFactory.WithKinds(), new ReferenceDataEntityNameResolver(refData));
        vmU.SelectedRow = vmU.AllAbilities.Single();
        var u = vmU.DetailViewModel!.ItemKeywordReqSetRefs[0];
        u.SetRef.IsActionable.Should().BeFalse();
        u.Activate.Should().BeNull();
        SetRef.ResolveClick(u.SetRef).Should().Be(SetRefClickAction.Unavailable);
    }

    [Fact]
    public void LinkProjections_PrerequisiteAndRosters_MirrorLegacyChips()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability { InternalName = "Base", Name = "Base", Skill = "Sword", Level = 1 },
                ["ability_2"] = new Ability { InternalName = "Up", Name = "Up", Skill = "Sword", Level = 9, Prerequisite = "Base" },
            },
        };
        var vm = new AbilitiesTabViewModel(
            refData, NavFactory.WithKinds(EntityKind.Ability), new ReferenceDataEntityNameResolver(refData));
        vm.SelectedRow = vm.AllAbilities.Single(r => r.InternalName == "Up");
        var d = vm.DetailViewModel!;

        d.PrerequisiteLink.Should().NotBeNull();
        d.PrerequisiteLink!.DisplayName.Should().Be(d.PrerequisiteChip!.DisplayName);
        d.PrerequisiteLink.Glyph.Should().Be(LinkGlyph.CombatAbility);
        d.PrerequisiteLink.IsNavigable.Should().Be(d.PrerequisiteChip.IsNavigable);
    }

    [Fact]
    public void FlagGroups_AreInertGrids_SelfHidingWhenEmpty()
    {
        var refData = new StubReferenceData
        {
            AbilitiesByKey =
            {
                ["ability_1"] = new Ability
                {
                    InternalName = "U", Name = "U", Skill = "Sword", Level = 1,
                    WorksUnderwater = true, WorksWhileFalling = true,
                },
            },
        };
        var vm = new AbilitiesTabViewModel(
            refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()),
            new ReferenceDataEntityNameResolver(refData));
        vm.SelectedRow = vm.AllAbilities.Single();
        var d = vm.DetailViewModel!;

        d.EnvironmentalFact.Layout.Should().Be(FactTableLayout.Grid);
        d.EnvironmentalFact.Pairs.Select(p => p.Label)
            .Should().Equal(d.EnvironmentalFlags.Select(f => f.Label));
        d.PetFact.StripText.Should().BeEmpty("no pet flags ⇒ Grid self-hides");
    }

    private static IReferenceDataService? BuildRealRefData(string bundled)
    {
        try
        {
            var cacheDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(cacheDir);
            using var http = new System.Net.Http.HttpClient(new ThrowingHttpHandler());
            return new ReferenceDataService(cacheDir, http, bundledDir: bundled);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ThrowingHttpHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("HTTP must not be called in this test");
    }

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<string, Ability> AbilitiesByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Npc> NpcsByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SkillEntry> SkillsMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Ability>> AbilitiesInGroupMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Ability>> AbilitiesUpgradingFromMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<AbilitySource>> AbilitySourcesMap { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>(StringComparer.Ordinal);
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills => SkillsMap;
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName => NpcsByKey;
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Quest> Quests { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public IReadOnlyDictionary<string, Ability> Abilities => AbilitiesByKey;
        public IReadOnlyDictionary<string, Ability> AbilitiesByInternalName =>
            AbilitiesByKey.Values
                .Where(a => !string.IsNullOrEmpty(a.InternalName))
                .ToDictionary(a => a.InternalName!, StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesInGroup => AbilitiesInGroupMap;
        public IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesUpgradingFrom => AbilitiesUpgradingFromMap;
        public IReadOnlyDictionary<string, IReadOnlyList<AbilitySource>> AbilitySources => AbilitySourcesMap;

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated;

        public void RaiseFileUpdated(string fileKey) => FileUpdated?.Invoke(this, fileKey);
    }
}
