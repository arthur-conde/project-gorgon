using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class RecipeIngredientItemKindTargetTests
{
    [Fact]
    public void Kind_IsRecipeIngredientItem()
    {
        var (target, _) = Build();
        target.Kind.Should().Be(EntityKind.RecipeIngredientItem);
    }

    [Fact]
    public void TabIndex_IsOne()
    {
        var (target, _) = Build();
        target.TabIndex.Should().Be(1);
    }

    [Fact]
    public void TryOpenInWindow_ReturnsFalse()
    {
        var (target, _) = Build();
        target.TryOpenInWindow().Should().BeFalse();
    }

    [Fact]
    public void TrySelectByInternalName_SetsIngredientsContainsFilter()
    {
        var (target, vm) = Build();

        var ok = target.TrySelectByInternalName("MetalSlab1");

        ok.Should().BeTrue();
        vm.QueryText.Should().Be("Ingredients CONTAINS \"MetalSlab1\"");
    }

    [Fact]
    public void TrySelectByInternalName_ClearsPriorSelection_SoFilteredListHasNoStaleRow()
    {
        var recipe = new Recipe { Key = "recipe_1", InternalName = "MakeSlab", Name = "Make Slab", Ingredients = [] };
        var (target, vm) = Build(recipe);
        vm.SelectedRow = vm.AllRecipes.Single();
        vm.SelectedRow.Should().NotBeNull();

        target.TrySelectByInternalName("MetalSlab1").Should().BeTrue();

        vm.SelectedRow.Should().BeNull(because: "filter-only navigation must drop any stale detail selection");
    }

    [Fact]
    public void TrySelectByInternalName_QuotesItemName_SoInternalNamesWithSpecialCharsParse()
    {
        // InternalNames in PG are ASCII identifiers, but quoting is the safe default — mirrors
        // RecipeIngredientKeywordKindTarget's behaviour so future schema oddities don't break it.
        var (target, vm) = Build();

        target.TrySelectByInternalName("Item With Spaces").Should().BeTrue();

        vm.QueryText.Should().Be("Ingredients CONTAINS \"Item With Spaces\"");
    }

    private static (RecipeIngredientItemKindTarget Target, RecipesTabViewModel Vm) Build(params Recipe[] recipes)
    {
        var refData = new FakeReferenceData();
        foreach (var recipe in recipes)
            refData.AddRecipe(recipe);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new RecipesTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData));
        return (new RecipeIngredientItemKindTarget(vm), vm);
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, Recipe> _recipes = new(StringComparer.Ordinal);

        public void AddRecipe(Recipe recipe) => _recipes[recipe.Key] = recipe;

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes => _recipes;
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
