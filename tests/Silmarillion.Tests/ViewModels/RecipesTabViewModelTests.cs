using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

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

        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator());

        vm.AllRecipes.Should().HaveCount(2);
        vm.AllRecipes.Select(r => r.Name).Should().Equal("Apple Sauce", "Bake Bread");
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
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator());

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
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator());

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
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator());

        vm.SelectedRecipe = recipe;

        vm.DetailViewModel!.Ingredients.Should().HaveCount(1);
        var chip = vm.DetailViewModel.Ingredients[0];
        chip.Reference.Should().Be(EntityRef.Item("Tomato"));
        chip.IsNavigable.Should().BeTrue();
        chip.DisplayName.Should().Contain("Tomato");
        chip.DisplayName.Should().Contain("3");
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
        var vm = new RecipesTabViewModel(refData, new SilmarillionReferenceNavigator());

        vm.SelectedRecipe = protoRecipe;

        vm.DetailViewModel!.ProducedItems.Should().ContainSingle()
            .Which.Reference.Should().Be(EntityRef.Item("TomatoSauce"));
    }

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<long, Item> ItemsByCode { get; } = new();
        public Dictionary<string, Recipe> RecipesByKey { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items => ItemsByCode;
        public IReadOnlyDictionary<string, Item> ItemsByInternalName =>
            ItemsByCode.Values.Where(i => i.InternalName is not null).ToDictionary(i => i.InternalName!);
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes => RecipesByKey;
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
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
