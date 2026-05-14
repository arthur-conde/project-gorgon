using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Query;
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

        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

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

        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

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

        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

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
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

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
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

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
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item));

        vm.SelectedRecipe = recipe;

        vm.DetailViewModel!.Ingredients.Should().HaveCount(1);
        var chip = vm.DetailViewModel.Ingredients[0];
        chip.Reference.Should().Be(EntityRef.Item("Tomato"));
        chip.IsNavigable.Should().BeTrue();
        chip.DisplayName.Should().Contain("Tomato");
        chip.DisplayName.Should().Contain("3");
    }

    [Fact]
    public void IngredientChips_IncludeKeywordIngredient_AlongsideItemIngredient()
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
                new RecipeItemIngredient { ItemCode = 100, StackSize = 1 },
                new RecipeKeywordIngredient { ItemKeys = ["Crystal"], Desc = "Auxiliary Crystal", StackSize = 1 },
            },
        };
        var refData = new StubReferenceData
        {
            ItemsByCode = { [100] = tomato },
            RecipesByKey = { ["r1"] = recipe },
        };
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item));

        vm.SelectedRecipe = recipe;

        vm.DetailViewModel!.Ingredients.Should().HaveCount(2);

        var keywordChip = vm.DetailViewModel.Ingredients[1];
        keywordChip.DisplayName.Should().Be("Auxiliary Crystal");
        keywordChip.IsNavigable.Should().BeFalse();
        keywordChip.IconId.Should().Be(0);
    }

    [Fact]
    public void IngredientChips_SingletonKeywordSlot_WithItemKeywordKindRegistered_IsNavigable()
    {
        // Symmetric to the item-detail "Used as" chip → recipe-tab filter direction
        // (PR #267): a recipe's singleton keyword slot now navigates back to the Items
        // tab filtered by that keyword. Chip stays inert (existing test above) when
        // the ItemKeyword kind isn't registered with the navigator.
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
        var refData = new StubReferenceData { RecipesByKey = { ["r1"] = recipe } };
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.ItemKeyword));

        vm.SelectedRecipe = recipe;

        var chip = vm.DetailViewModel!.Ingredients.Single();
        chip.IsNavigable.Should().BeTrue();
        chip.Reference.Should().Be(EntityRef.ItemKeyword("Crystal"));
        chip.DisplayName.Should().Be("Auxiliary Crystal");
    }

    [Fact]
    public void IngredientChips_CompositeKeywordSlot_AllMappable_IsNavigable()
    {
        // Composite slot every key of which is translatable (bare tag + EquipmentSlot:)
        // → mapper succeeds → AND-joined query, chip is navigable. Today's catalogue
        // doesn't ship a slot of this exact shape but the mapping covers any future one.
        var recipe = new Recipe
        {
            Key = "r1",
            InternalName = "Hypothetical",
            Name = "Hypothetical",
            Skill = "Augmentation",
            Ingredients = new RecipeIngredient[]
            {
                new RecipeKeywordIngredient { ItemKeys = ["EquipmentSlot:MainHand", "Crystal"], Desc = "Crystal Main-Hand", StackSize = 1 },
            },
        };
        var refData = new StubReferenceData { RecipesByKey = { ["r1"] = recipe } };
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.ItemKeyword));

        vm.SelectedRecipe = recipe;

        var chip = vm.DetailViewModel!.Ingredients.Single();
        chip.IsNavigable.Should().BeTrue();
        chip.Reference.Should().Be(EntityRef.ItemKeyword(["EquipmentSlot:MainHand", "Crystal"]));
        chip.DisplayName.Should().Be("Crystal Main-Hand");
    }

    [Fact]
    public void IngredientChips_CompositeKeywordSlot_WithUnmappableKey_StaysNonNavigable()
    {
        // Real catalogue pattern: Decompose Main-Hand Weapon's slot is
        // ["EquipmentSlot:MainHand", "MinTSysPrereq:0"]. MinTSysPrereq:N has no item-side
        // analogue today, so the mapper fails and the chip stays inert even with the
        // ItemKeyword kind target registered. The Reference is still emitted so a future
        // mapping expansion (e.g. item-side TSys prereq exposure) flips the chip live
        // with no chip-builder change.
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
        var refData = new StubReferenceData { RecipesByKey = { ["r1"] = recipe } };
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.ItemKeyword));

        vm.SelectedRecipe = recipe;

        var chip = vm.DetailViewModel!.Ingredients.Single();
        chip.IsNavigable.Should().BeFalse(because: "MinTSysPrereq:0 has no item-side mapping");
        chip.Reference.Should().Be(EntityRef.ItemKeyword(["EquipmentSlot:MainHand", "MinTSysPrereq:0"]));
        chip.DisplayName.Should().Be("Main-Hand Item");
    }

    [Fact]
    public void IngredientChips_KeywordIngredient_WithoutDesc_FallsBackToHumanisedItemKeys()
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
        };
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedRecipe = recipe;

        vm.DetailViewModel!.Ingredients.Should().ContainSingle()
            .Which.DisplayName.Should().Be("any Crystal + Tier3");
    }

    [Fact]
    public void IngredientChips_KeywordIngredient_WithEmptyItemKeys_IsSkipped()
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
        var refData = new StubReferenceData
        {
            RecipesByKey = { ["r1"] = recipe },
        };
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedRecipe = recipe;

        vm.DetailViewModel!.Ingredients.Should().BeEmpty();
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
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Item, EntityKind.Recipe));

        vm.SelectedRecipe = recipe;

        vm.DetailViewModel!.Sources.Should().NotBeNull();
        vm.DetailViewModel.Sources!.Should().ContainSingle();
        var chip = vm.DetailViewModel.Sources![0];
        chip.DisplayName.Should().Be("Training: NPC_Marna");
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
        var vm = new RecipesTabViewModel(refData, NavFactory.WithKinds(EntityKind.Recipe));

        vm.SelectedRecipe = recipe;

        var sources = vm.DetailViewModel!.Sources!;
        sources.Should().HaveCount(3);

        sources[0].DisplayName.Should().Be("Training: NPC_Fritz");
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
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

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

        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        var row = vm.AllRecipes.Should().ContainSingle().Subject;
        row.IngredientKeywords.Select(k => k.Tag)
            .Should().BeEquivalentTo(["Crystal", "Tier2"]);
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
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

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
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        // The exact query string produced by RecipeIngredientKeywordKindTarget.TrySelectByInternalName.
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

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items => ItemsByCode;
        public IReadOnlyDictionary<string, Item> ItemsByInternalName =>
            ItemsByCode.Values.Where(i => i.InternalName is not null).ToDictionary(i => i.InternalName!);
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
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
        public IReadOnlyDictionary<string, QuestEntry> Quests { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
