using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Query;
using Mithril.TestSupport;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

// Helper to build a navigator with specific kinds registered (needed for IsNavigable assertions).
file static class NavFactory
{
    /// <summary>Creates a navigator with stub targets for the given entity kinds.</summary>
    public static SilmarillionReferenceNavigator WithKinds(params EntityKind[] kinds) =>
        new SilmarillionReferenceNavigator(kinds.Select(k => (IReferenceKindTarget)new StubKindTarget(k)));

    private sealed class StubKindTarget : IReferenceKindTarget
    {
        public StubKindTarget(EntityKind kind) => Kind = kind;
        public EntityKind Kind { get; }
        public int TabIndex => 0;
        public bool TrySelectByInternalName(string internalName) => true;
        public bool TryOpenInWindow() => false;
    }
}

public sealed class RecipesTabViewModelTests
{
    [Fact]
    public void AllRecipes_PopulatedFromReferenceData_OrderedByName()
    {
        var refData = new StubReferenceData
        {
            RecipesByKey =
            {
                ["r1"] = new Recipe { Key = "r1", Name = "Bake Bread", Ingredients = [] },
                ["r2"] = new Recipe { Key = "r2", Name = "Apple Sauce", Ingredients = [] },
            },
        };

        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.AllRecipes.Should().HaveCount(2);
        vm.AllRecipes.Select(r => r.Name).Should().Equal("Apple Sauce", "Bake Bread");
    }

    [Fact]
    public void RecipeListRow_ResolvesSkillKey_ToDisplayName_WhenPresent()
    {
        var refData = new StubReferenceData
        {
            SkillsByKey =
            {
                ["AncillaryArmorAugmentBrewing"] = new SkillEntry(
                    Key: "AncillaryArmorAugmentBrewing",
                    DisplayName: "Armor Augment Brewing",
                    Id: 1,
                    Combat: false,
                    XpTable: "",
                    MaxBonusLevels: 0,
                    Parents: [],
                    Rewards: new Dictionary<string, SkillRewardEntry>()),
            },
            RecipesByKey =
            {
                ["r1"] = new Recipe { Key = "r1", Name = "A", Skill = "AncillaryArmorAugmentBrewing", SkillLevelReq = 30, Ingredients = [] },
            },
        };

        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.AllRecipes.Should().ContainSingle()
            .Which.SkillDisplayName.Should().Be("Armor Augment Brewing");
    }

    [Fact]
    public void RecipeListRow_FallsBack_ToSkillKey_WhenUnresolved()
    {
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["r1"] = new Recipe { Key = "r1", Name = "A", Skill = "UnknownSkill", SkillLevelReq = 1, Ingredients = [] } },
        };

        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.AllRecipes.Should().ContainSingle()
            .Which.SkillDisplayName.Should().Be("UnknownSkill");
    }

    [Fact]
    public void SelectingRecipe_BuildsDetailViewModel()
    {
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "MakeTomatoSauce",
            Name = "Make Tomato Sauce",
            Skill = "Cooking",
            SkillLevelReq = 12,
            Ingredients = [],
        };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["r1"] = recipe },
        };
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.Recipe.Should().Be(recipe);
    }

    [Fact]
    public void DeselectingRecipe_ClearsDetailViewModel()
    {
        var recipe = new Recipe { Key = "r1", Name = "X", Ingredients = [] };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["r1"] = recipe },
        };
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;
        vm.SelectedRecipe = null;

        vm.DetailViewModel.Should().BeNull();
    }

    [Fact]
    public void IngredientChips_ResolveItemCodeToInternalName()
    {
        var tomato = new Item { Id = 100, InternalName = "Tomato", Name = "Tomato", IconId = 1 };
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "MakeSauce",
            Name = "Make Sauce",
            Skill = "Cooking",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeItemIngredient { ItemCode = 100, StackSize = 3 },
            },
        };
        var refData = new StubReferenceData
        {
            ItemsByCode = { [100] = tomato },
            RecipesByKey = { ["r1"] = recipe },
        };
        // Use a navigator that has Item kind registered so CanOpen returns true for items.
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        vm.DetailViewModel!.Ingredients.Should().HaveCount(1);
        var chip = vm.DetailViewModel.Ingredients[0];
        chip.Reference.Should().Be(EntityRef.Item("Tomato"));
        chip.IsNavigable.Should().BeTrue();
        chip.DisplayName.Should().Contain("Tomato");
        chip.DisplayName.Should().Contain("3");
    }

    // #318 slice 4, surface 3 — the recipe-detail keyword surface is now a provenance
    // popup, not a synthetic-kind chip. Keyword slots are NO LONGER in Ingredients (which
    // is item-ingredient chips only, 1:1); they surface as DetailViewModel.KeywordSlots
    // rows fed IReferenceDataService.ItemsByRecipeKeywordSlotWithReason directly. The
    // retired ItemKeyword EntityKind / ItemKeywordQueryMapper tests are deleted with them.

    [Fact]
    public void Ingredients_ItemSlotsOnly_KeywordSlotsExcluded()
    {
        var tomato = new Item { Id = 100, InternalName = "Tomato", Name = "Tomato", IconId = 1 };
        var crystal = new Item { Id = 200, InternalName = "RoughCrystal", Name = "Rough Crystal" };
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "MakeSauce",
            Name = "Make Sauce",
            Skill = "Cooking",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeItemIngredient { ItemCode = 100, StackSize = 1 },
                new RecipeKeywordIngredient { ItemKeys = ["Crystal"], Desc = "Auxiliary Crystal", StackSize = 1 },
            },
        };
        var refData = new StubReferenceData
        {
            ItemsByCode = { [100] = tomato },
            RecipesByKey = { ["r1"] = recipe },
            KeywordSlotMatches =
            {
                ["Crystal"] = new[] { new RecipeKeywordItemMatch(crystal, RecipeKeywordItemMatchReason.KeywordMatch) },
            },
        };
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        // Only the item ingredient chip is in Ingredients — the keyword slot moved out.
        vm.DetailViewModel!.Ingredients.Should().ContainSingle()
            .Which.Reference.Should().Be(EntityRef.Item("Tomato"));
        vm.DetailViewModel.KeywordSlots.Should().ContainSingle();
        var slot = vm.DetailViewModel.KeywordSlots[0];
        slot.Label.Should().Be("Auxiliary Crystal");
        slot.MatchCount.Should().Be(1);
        slot.Popup.IsFlat.Should().BeTrue(because: "single-reason ⇒ flat list (#318 Discipline)");
        slot.Popup.FlatChips.Should().ContainSingle()
            .Which.Reference.Should().Be(EntityRef.Item("RoughCrystal"));
    }

    [Fact]
    public void KeywordSlot_PopupMembership_EqualsIndex_AndChipsAreNavigableWhenItemKindRegistered()
    {
        // Membership == the index collection (no query re-derivation — the #318
        // invariant). Chips inside the popup are 1:1 Item references, navigable iff the
        // Items kind is registered.
        var a = new Item { Id = 1, InternalName = "ZItem", Name = "Z Item", IconId = 9 };
        var b = new Item { Id = 2, InternalName = "AItem", Name = "A Item" };
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "EnchantCrystal",
            Name = "Enchant Crystal",
            Skill = "Enchanting",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeKeywordIngredient { ItemKeys = ["Crystal"], Desc = "Auxiliary Crystal", StackSize = 1 },
            },
        };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["r1"] = recipe },
            KeywordSlotMatches =
            {
                ["Crystal"] = new[]
                {
                    new RecipeKeywordItemMatch(a, RecipeKeywordItemMatchReason.KeywordMatch),
                    new RecipeKeywordItemMatch(b, RecipeKeywordItemMatchReason.KeywordMatch),
                },
            },
        };
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        var slot = vm.DetailViewModel!.KeywordSlots.Single();
        // Distinct count == index members; popup chips are exactly the index items
        // (ordered by display name), all 1:1 navigable Item refs.
        slot.MatchCount.Should().Be(2);
        slot.Popup.TotalCount.Should().Be(2);
        slot.Popup.FlatChips.Select(c => c.Reference)
            .Should().Equal(EntityRef.Item("AItem"), EntityRef.Item("ZItem"));
        slot.Popup.FlatChips.Should().OnlyContain(c => c.IsNavigable);
    }

    [Fact]
    public void KeywordSlot_CompositeSlotKey_IsJoinedWithPlus_ForIndexLookup()
    {
        // The slot key is the ItemKeys list '+'-joined (the retired EntityRef.ItemKeyword
        // encoding, kept stable across the migration so the index key form doesn't shift).
        var match = new Item { Id = 1, InternalName = "Wand", Name = "Wand" };
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "DecomposeMainHand",
            Name = "Decompose Main-Hand Weapon",
            Skill = "WeaponAugmentBrewing",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeKeywordIngredient { ItemKeys = ["EquipmentSlot:MainHand", "MinTSysPrereq:0"], Desc = "Main-Hand Item", StackSize = 1 },
            },
        };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["r1"] = recipe },
            KeywordSlotMatches =
            {
                ["EquipmentSlot:MainHand+MinTSysPrereq:0"] =
                    new[] { new RecipeKeywordItemMatch(match, RecipeKeywordItemMatchReason.KeywordMatch) },
            },
        };
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        var slot = vm.DetailViewModel!.KeywordSlots.Single();
        slot.Label.Should().Be("Main-Hand Item");
        slot.MatchCount.Should().Be(1);
        slot.Popup.FlatChips.Single().Reference.Should().Be(EntityRef.Item("Wand"));
    }

    [Fact]
    public void KeywordSlot_WithoutDesc_LabelFallsBackToHumanisedItemKeys()
    {
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "EnchantSomething",
            Name = "Enchant Something",
            Skill = "Enchanting",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeKeywordIngredient { ItemKeys = ["Crystal", "Tier3"], Desc = null, StackSize = 1 },
            },
        };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["r1"] = recipe },
            KeywordSlotMatches = { ["Crystal+Tier3"] = Array.Empty<RecipeKeywordItemMatch>() },
        };
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        var slot = vm.DetailViewModel!.KeywordSlots.Should().ContainSingle().Which;
        slot.Label.Should().Be("any Crystal + Tier3");
        slot.MatchCount.Should().Be(0, because: "the slot is keyed in the index with an empty match list");
    }

    [Fact]
    public void KeywordSlot_EmptyItemKeys_IsSkipped()
    {
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "DegenerateRecipe",
            Name = "Degenerate",
            Skill = "Cooking",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeKeywordIngredient { ItemKeys = [], Desc = null, StackSize = 1 },
            },
        };
        var refData = new StubReferenceData { RecipesByKey = { ["r1"] = recipe } };
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        vm.DetailViewModel!.Ingredients.Should().BeEmpty();
        vm.DetailViewModel.KeywordSlots.Should().BeEmpty();
    }

    [Fact]
    public void KeywordSlot_NotInIndex_IsSkipped_NoDeadRow()
    {
        // A slot whose keys aren't in the provenance index (data shifted between
        // item/recipe loads) is skipped rather than rendering a dead row.
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "Mystery",
            Name = "Mystery",
            Skill = "Cooking",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeKeywordIngredient { ItemKeys = ["UnknownKw"], Desc = "Unknown", StackSize = 1 },
            },
        };
        var refData = new StubReferenceData { RecipesByKey = { ["r1"] = recipe } };
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        vm.DetailViewModel!.KeywordSlots.Should().BeEmpty();
    }

    [Fact]
    public void KeywordSlot_OpeningPopup_PushesNoNavigatorHistory()
    {
        // #318 #229 — opening the popup must NOT push navigator back/forward history.
        // The opener is swapped for a capturing no-op so no window spawns; navigator
        // state is pristine before and after (mirrors the surface-1 ItemDetail test).
        var match = new Item { Id = 1, InternalName = "Wand", Name = "Wand" };
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "EnchantCrystal",
            Name = "Enchant Crystal",
            Skill = "Enchanting",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeKeywordIngredient { ItemKeys = ["Crystal"], Desc = "Auxiliary Crystal", StackSize = 1 },
            },
        };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["r1"] = recipe },
            KeywordSlotMatches =
            {
                ["Crystal"] = new[] { new RecipeKeywordItemMatch(match, RecipeKeywordItemMatchReason.KeywordMatch) },
            },
        };
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new RecipesTabViewModel(refData, nav, new FakeEntityNameResolver());

        var prior = RecipeDetailViewModel.ProvenancePopupOpener;
        Mithril.Shared.Wpf.ProvenancePopupViewModel? captured = null;
        RecipeDetailViewModel.ProvenancePopupOpener = (popupVm, _) => captured = popupVm;
        try
        {
            vm.SelectedRecipe = recipe;
            var slot = vm.DetailViewModel!.KeywordSlots.Single();

            nav.Current.Should().BeNull();
            nav.CanGoBack.Should().BeFalse();
            nav.CanGoForward.Should().BeFalse();

            slot.ShowPopupCommand.Execute(null);

            captured.Should().NotBeNull(because: "the command invoked the opener with the built popup VM");
            captured!.Should().BeSameAs(slot.Popup);
            // The defining assertion: opening the popup pushed no navigator state.
            nav.Current.Should().BeNull();
            nav.CanGoBack.Should().BeFalse();
            nav.CanGoForward.Should().BeFalse();
        }
        finally
        {
            RecipeDetailViewModel.ProvenancePopupOpener = prior;
        }
    }

    // ── G4 Stacking semantics (docs/silmarillion-visual-grammar.md ·
    //    "Stacking semantics") ─────────────────────────────────────────────────
    // Recipe keyword-ingredient slots are positionally MATERIAL whenever ≥2 (the
    // grammar's own TSysCraftedEquipment canonical example). ≥2 ⇒ each slot's
    // SetRefVm.SlotOrdinal = its 1-based position + the section label gets a
    // "· N slots" suffix. 0/1 slot ⇒ null ordinal + plain label. The Recipe pilot
    // does not exercise the positionally-inert (count-only) path.

    [Fact]
    public void KeywordSlots_TwoOrMore_AreMaterial_OrdinalsAndLabelSuffix()
    {
        // The grammar's canonical example: a crafted-equipment recipe with two
        // "any Crystal" slots. Constraints are IDENTICAL — materiality must NOT
        // be gated on "same constraint"; ≥2 slots ⇒ material per the clause.
        var a = new Item { Id = 1, InternalName = "RoughCrystal", Name = "Rough Crystal" };
        var b = new Item { Id = 2, InternalName = "FineCrystal", Name = "Fine Crystal" };
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "ForgeCrystalBlade",
            Name = "Forge Crystal Blade",
            Skill = "Blacksmithing",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeKeywordIngredient { ItemKeys = ["Crystal"], Desc = "Primary Crystal", StackSize = 1 },
                new RecipeKeywordIngredient { ItemKeys = ["Crystal"], Desc = "Secondary Crystal", StackSize = 1 },
            },
        };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["r1"] = recipe },
            KeywordSlotMatches =
            {
                ["Crystal"] = new[]
                {
                    new RecipeKeywordItemMatch(a, RecipeKeywordItemMatchReason.KeywordMatch),
                    new RecipeKeywordItemMatch(b, RecipeKeywordItemMatchReason.KeywordMatch),
                },
            },
        };
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        var slots = vm.DetailViewModel!.KeywordSlots;
        slots.Should().HaveCount(2);
        // 1-based ordinals flow into the SetRefVm.
        slots[0].SetRef.SlotOrdinal.Should().Be(1);
        slots[1].SetRef.SlotOrdinal.Should().Be(2);
        slots[0].SetRef.HasOrdinal.Should().BeTrue();
        slots[0].SetRef.OrdinalText.Should().Be("1");
        slots[1].SetRef.OrdinalText.Should().Be("2");
        // Section-label suffix is VM-owned.
        vm.DetailViewModel.KeywordIngredientsLabel.Should().Be("Keyword ingredients · 2 slots");
    }

    [Fact]
    public void KeywordSlots_ThreeSlots_OrdinalsAreOneTwoThree_LabelSaysThreeSlots()
    {
        var item = new Item { Id = 1, InternalName = "Gem", Name = "Gem" };
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "TripleSocket",
            Name = "Triple Socket",
            Skill = "Jewelry",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeKeywordIngredient { ItemKeys = ["Gem"], Desc = "Slot A", StackSize = 1 },
                new RecipeKeywordIngredient { ItemKeys = ["Gem"], Desc = "Slot B", StackSize = 1 },
                new RecipeKeywordIngredient { ItemKeys = ["Gem"], Desc = "Slot C", StackSize = 1 },
            },
        };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["r1"] = recipe },
            KeywordSlotMatches =
            {
                ["Gem"] = new[] { new RecipeKeywordItemMatch(item, RecipeKeywordItemMatchReason.KeywordMatch) },
            },
        };
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        var slots = vm.DetailViewModel!.KeywordSlots;
        slots.Select(s => s.SetRef.SlotOrdinal).Should().Equal(1, 2, 3);
        vm.DetailViewModel.KeywordIngredientsLabel.Should().Be("Keyword ingredients · 3 slots");
    }

    [Fact]
    public void KeywordSlots_OrdinalGate_KeysOffSurvivingCount_NotRawIngredientPosition()
    {
        // A skipped slot (keys not in the index) must not consume an ordinal: the
        // ≥2-material gate and the 1-based numbering key off the FINAL surviving
        // list. Here 3 keyword ingredients are authored but one is not in the
        // index → 2 survive → still material, ordinals 1,2 (no gap at the skip).
        var item = new Item { Id = 1, InternalName = "Crystal", Name = "Crystal" };
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "GappedRecipe",
            Name = "Gapped Recipe",
            Skill = "Blacksmithing",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeKeywordIngredient { ItemKeys = ["Crystal"], Desc = "First", StackSize = 1 },
                new RecipeKeywordIngredient { ItemKeys = ["MissingKw"], Desc = "Skipped", StackSize = 1 },
                new RecipeKeywordIngredient { ItemKeys = ["Crystal"], Desc = "Second", StackSize = 1 },
            },
        };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["r1"] = recipe },
            KeywordSlotMatches =
            {
                ["Crystal"] = new[] { new RecipeKeywordItemMatch(item, RecipeKeywordItemMatchReason.KeywordMatch) },
                // "MissingKw" deliberately absent from the index ⇒ that slot is skipped.
            },
        };
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        var slots = vm.DetailViewModel!.KeywordSlots;
        slots.Should().HaveCount(2, because: "the not-in-index slot is skipped, never reaches the list");
        slots.Select(s => s.Label).Should().Equal("First", "Second");
        slots.Select(s => s.SetRef.SlotOrdinal).Should().Equal(1, 2);
        vm.DetailViewModel.KeywordIngredientsLabel.Should().Be("Keyword ingredients · 2 slots");
    }

    [Fact]
    public void KeywordSlots_SingleSlot_IsInert_NoOrdinal_PlainLabel()
    {
        // Exactly 1 surviving slot ⇒ positionally inert path: SlotOrdinal null,
        // no prefix, the section label stays plain (no "· N slots" suffix).
        var match = new Item { Id = 1, InternalName = "RoughCrystal", Name = "Rough Crystal" };
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "SingleCrystal",
            Name = "Single Crystal",
            Skill = "Enchanting",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeKeywordIngredient { ItemKeys = ["Crystal"], Desc = "Auxiliary Crystal", StackSize = 1 },
            },
        };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["r1"] = recipe },
            KeywordSlotMatches =
            {
                ["Crystal"] = new[] { new RecipeKeywordItemMatch(match, RecipeKeywordItemMatchReason.KeywordMatch) },
            },
        };
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        var slot = vm.DetailViewModel!.KeywordSlots.Should().ContainSingle().Which;
        slot.SetRef.SlotOrdinal.Should().BeNull();
        slot.SetRef.HasOrdinal.Should().BeFalse();
        slot.SetRef.OrdinalText.Should().BeEmpty();
        vm.DetailViewModel.KeywordIngredientsLabel.Should().Be("Keyword ingredients");
    }

    [Fact]
    public void KeywordSlots_NoSlots_PlainLabel_SectionHiddenUnchanged()
    {
        // 0 slots ⇒ no ordinals, plain label, and the empty list still drives the
        // section hide in the view (KeywordSlots.Count == 0) — behaviour unchanged.
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "NoKeywordRecipe",
            Name = "No Keyword Recipe",
            Skill = "Cooking",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeItemIngredient { ItemCode = 100, StackSize = 1 },
            },
        };
        var refData = new StubReferenceData
        {
            ItemsByCode = { [100] = new Item { Id = 100, InternalName = "Tomato", Name = "Tomato" } },
            RecipesByKey = { ["r1"] = recipe },
        };
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        vm.DetailViewModel!.KeywordSlots.Should().BeEmpty();
        vm.DetailViewModel.KeywordIngredientsLabel.Should().Be("Keyword ingredients");
    }

    [Fact]
    public void SourceChips_NpcTrainerSource_NotNavigable_UntilNpcKindShips()
    {
        // Recipe taught by a single NPC trainer → one ItemSourceChipVm chip with the NPC
        // EntityRef attached but IsNavigable=false because the NPCs tab hasn't shipped yet.
        // The moment a kind target for Npc is registered (#241), the same chip flips to
        // clickable with no code change here.
        var recipe = new Recipe
        {
            Key = "recipe_1",
            InternalName = "MakeTomatoSauce",
            Name = "Make Tomato Sauce",
            Skill = "Cooking",
            Ingredients = [],
        };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["recipe_1"] = recipe },
            RecipeSourcesByName =
            {
                ["MakeTomatoSauce"] = new[]
                {
                    new RecipeSource("Training", "NPC_Marna", null),
                },
            },
        };
        // Navigator has Recipe + Item kinds, but NOT Npc — matches v1 ship state.
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item, EntityKind.Recipe), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRecipe = recipe;

        vm.DetailViewModel!.Sources.Should().NotBeNull();
        vm.DetailViewModel.Sources!.Should().ContainSingle();
        var chip = vm.DetailViewModel.Sources![0];
        // No NPC POCO seeded → ReferenceDataEntityNameResolver strips the "NPC_" prefix.
        chip.DisplayName.Should().Be("Training: Marna");
        chip.EntityReference.Should().Be(EntityRef.Npc("NPC_Marna"));
        chip.IsNavigable.Should().BeFalse();
    }

    [Fact]
    public void SourceChips_MixedKinds_ProjectInOrder_WithCorrectShapePerKind()
    {
        // Recipe with three source kinds:
        //   1. Training (NPC chip) — NPC ref attached, non-navigable in v1.
        //   2. Skill — no NPC, Context carries the skill name as Detail.
        //   3. Effect — no NPC, no Context (scroll-derived recipes don't carry it
        //      yet; reverse-resolve is #252-style follow-up). Plain-text chip.
        var recipe = new Recipe
        {
            Key = "recipe_99",
            InternalName = "ScrollOfFireball",
            Name = "Scroll of Fireball",
            Skill = "Mentalism",
            Ingredients = [],
        };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["recipe_99"] = recipe },
            RecipeSourcesByName =
            {
                ["ScrollOfFireball"] = new[]
                {
                    new RecipeSource("Training", "NPC_Fritz", null),
                    new RecipeSource("Skill", null, "Mentalism"),
                    new RecipeSource("Effect", null, null),
                },
            },
        };
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Recipe), new ReferenceDataEntityNameResolver(refData));

        vm.SelectedRecipe = recipe;

        var sources = vm.DetailViewModel!.Sources!;
        sources.Should().HaveCount(3);

        sources[0].DisplayName.Should().Be("Training: Fritz");
        sources[0].EntityReference.Should().Be(EntityRef.Npc("NPC_Fritz"));
        sources[0].IsNavigable.Should().BeFalse();

        sources[1].DisplayName.Should().Be("Skill");
        sources[1].EntityReference.Should().BeNull();
        sources[1].Detail.Should().Be("Mentalism");

        sources[2].DisplayName.Should().Be("Effect");
        sources[2].EntityReference.Should().BeNull();
        sources[2].Detail.Should().BeNull();
    }

    [Fact]
    public void SourceChips_RecipeWithNoSources_LeavesSourcesNull_ForXamlHide()
    {
        // The XAML uses PositiveIntToVis on Sources.Count to collapse the section;
        // a null Sources list therefore disappears the "Taught by" header entirely.
        var recipe = new Recipe { Key = "recipe_42", InternalName = "Untaught", Name = "Untaught", Ingredients = [] };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["recipe_42"] = recipe },
        };
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.SelectedRecipe = recipe;

        vm.DetailViewModel!.Sources.Should().BeNull();
    }

    [Fact]
    public void RecipeListRow_IngredientKeywords_flattens_RecipeKeywordIngredient_ItemKeys_distinctly()
    {
        // One recipe with two keyword slots: ["Crystal"] and ["Crystal", "Tier2"].
        // The flat, deduped set across all slots should be exactly {"Crystal", "Tier2"}.
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "EnchantCrystal",
            Name = "Enchant Crystal",
            Skill = "Enchanting",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeKeywordIngredient { ItemKeys = ["Crystal"], StackSize = 1 },
                new RecipeKeywordIngredient { ItemKeys = ["Crystal", "Tier2"], StackSize = 1 },
            },
        };
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["r1"] = recipe },
        };

        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        var row = vm.AllRecipes.Should().ContainSingle().Subject;
        row.IngredientKeywords.Select(k => k.Tag)
            .Should().BeEquivalentTo(["Crystal", "Tier2"]);
    }

    [Fact]
    public void RecipeListRow_Ingredients_flattens_RecipeItemIngredient_InternalNames_distinctly()
    {
        // Two direct-item slots referencing the same item code, plus a keyword slot
        // (which must be ignored — keywords go into IngredientKeywords, not Ingredients).
        var slab = new Item { Id = 42, InternalName = "MetalSlab1", Name = "Metal Slab" };
        var oil = new Item { Id = 7, InternalName = "OilTier1", Name = "Tier 1 Oil" };
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "ForgeSlab",
            Name = "Forge Slab",
            Skill = "Blacksmithing",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeItemIngredient { ItemCode = 42, StackSize = 1 },
                new RecipeItemIngredient { ItemCode = 42, StackSize = 2 }, // dup → collapses
                new RecipeItemIngredient { ItemCode = 7, StackSize = 1 },
                new RecipeKeywordIngredient { ItemKeys = ["Crystal"], StackSize = 1 }, // ignored
            },
        };
        var refData = new StubReferenceData
        {
            ItemsByCode = { [42] = slab, [7] = oil },
            RecipesByKey = { ["r1"] = recipe },
        };

        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        var row = vm.AllRecipes.Should().ContainSingle().Subject;
        row.Ingredients.Select(i => i.InternalName)
            .Should().BeEquivalentTo(["MetalSlab1", "OilTier1"]);
    }

    [Fact]
    public void QueryText_IngredientsContains_FiltersToMatchingRecipeOnly()
    {
        // Symmetric counterpart to the IngredientKeywords query test — verifies the same
        // query path works for the item-pivot direction (powers the "Used in" overflow pill).
        var slab = new Item { Id = 42, InternalName = "MetalSlab1", Name = "Metal Slab" };
        var hide = new Item { Id = 88, InternalName = "LeatherHide", Name = "Leather Hide" };
        var recipeA = new Recipe
        {
            Key = "rA", InternalName = "ForgeSlab", Name = "Forge Slab", Skill = "Blacksmithing",
            Ingredients = new RecipeIngredient[] { new RecipeItemIngredient { ItemCode = 42, StackSize = 1 } },
        };
        var recipeB = new Recipe
        {
            Key = "rB", InternalName = "TanHide", Name = "Tan Hide", Skill = "Tanning",
            Ingredients = new RecipeIngredient[] { new RecipeItemIngredient { ItemCode = 88, StackSize = 1 } },
        };
        var refData = new StubReferenceData
        {
            ItemsByCode = { [42] = slab, [88] = hide },
            RecipesByKey = { ["rA"] = recipeA, ["rB"] = recipeB },
        };
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        const string queryString = "Ingredients CONTAINS \"MetalSlab1\"";
        var columns = ColumnBindingHelper.BuildFromProperties(typeof(RecipeListRow));
        var predicate = QueryCompiler.Compile(queryString, columns);
        predicate.Should().NotBeNull();

        var matches = vm.AllRecipes.Where(row => predicate!(row)).ToList();

        matches.Should().ContainSingle(r => r.Recipe.InternalName == "ForgeSlab");
        matches.Should().NotContain(r => r.Recipe.InternalName == "TanHide");
    }

    [Fact]
    public void ProducedItemChips_PreferResultItems_FallBackToProtoResultItems()
    {
        var sauce = new Item { Id = 101, InternalName = "TomatoSauce", Name = "Tomato Sauce", IconId = 2 };
        var protoRecipe = new Recipe
        {
            Key = "r1",
            InternalName = "CraftSauce",
            Name = "Craft Sauce",
            Skill = "Cooking",
            Ingredients = [],
            ProtoResultItems = new[] { new RecipeResultItem { ItemCode = 101, StackSize = 1 } },
        };
        var refData = new StubReferenceData
        {
            ItemsByCode = { [101] = sauce },
            RecipesByKey = { ["r1"] = protoRecipe },
        };
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        vm.SelectedRecipe = protoRecipe;

        vm.DetailViewModel!.ProducedItems.Should().ContainSingle()
            .Which.Reference.Should().Be(EntityRef.Item("TomatoSauce"));
    }

    [Fact]
    public void QueryText_IngredientKeywordsContains_FiltersToMatchingRecipeOnly()
    {
        // Recipe A: has a keyword ingredient with "Crystal" — should match.
        var recipeA = new Recipe
        {
            Key = "rA",
            InternalName = "EnchantWithCrystal",
            Name = "Enchant With Crystal",
            Skill = "Enchanting",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeKeywordIngredient { ItemKeys = ["Crystal"], StackSize = 1 },
            },
        };
        // Recipe B: only item ingredients, no keyword slots — should NOT match.
        var recipeB = new Recipe
        {
            Key = "rB",
            InternalName = "BakeBread",
            Name = "Bake Bread",
            Skill = "Baking",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeItemIngredient { ItemCode = 999, StackSize = 2 },
            },
        };
        var refData = new StubReferenceData
        {
            RecipesByKey =
            {
                ["rA"] = recipeA,
                ["rB"] = recipeB,
            },
        };
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), new FakeEntityNameResolver());

        // The IngredientKeywords-CONTAINS query plumbing (collection-CONTAINS via
        // IQueryStringValue, #261) — still live for hand-typed queries and the reserved
        // ProvenancePopupViewModel.ToQueryCommand projection. (The synthetic
        // RecipeIngredientKeyword kind target that used to emit this was retired in #318
        // slice 4 surface 2; the "Used as" surface is now a provenance popup.)
        const string queryString = "IngredientKeywords CONTAINS \"Crystal\"";

        var columns = ColumnBindingHelper.BuildFromProperties(typeof(RecipeListRow));
        var predicate = QueryCompiler.Compile(queryString, columns);
        predicate.Should().NotBeNull("the query string must parse without error");

        var matches = vm.AllRecipes.Where(row => predicate!(row)).ToList();

        matches.Should().ContainSingle(r => r.Recipe.InternalName == "EnchantWithCrystal",
            "Recipe A has a Crystal keyword ingredient");
        matches.Should().NotContain(r => r.Recipe.InternalName == "BakeBread",
            "Recipe B has no keyword ingredients");
    }

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<long, Item> ItemsByCode { get; } = new();
        public Dictionary<string, Recipe> RecipesByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SkillEntry> SkillsByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<RecipeSource>> RecipeSourcesByName { get; } = new(StringComparer.Ordinal);
        // Surface-3 provenance index (#318 slice 4): slot-keys '+'-joined → matching items.
        public Dictionary<string, IReadOnlyList<RecipeKeywordItemMatch>> KeywordSlotMatches { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items => ItemsByCode;
        public IReadOnlyDictionary<string, Item> ItemsByInternalName =>
            ItemsByCode.Values.Where(i => i.InternalName is not null).ToDictionary(i => i.InternalName!);
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, IReadOnlyList<RecipeKeywordItemMatch>> ItemsByRecipeKeywordSlotWithReason => KeywordSlotMatches;
        public IReadOnlyDictionary<string, Recipe> Recipes => RecipesByKey;
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills => SkillsByKey;
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, IReadOnlyList<RecipeSource>> RecipeSources => RecipeSourcesByName;
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
