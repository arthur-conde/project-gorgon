using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class RecipesKindTargetTests
{
    [Fact]
    public void Kind_IsRecipe()
    {
        var (target, _, _) = BuildTarget();
        target.Kind.Should().Be(EntityKind.Recipe);
    }

    [Fact]
    public void TabIndex_IsOne()
    {
        var (target, _, _) = BuildTarget();
        target.TabIndex.Should().Be(1);
    }

    [Fact]
    public void TrySelectByInternalName_KnownRecipe_SelectsOnTabVm_ReturnsTrue()
    {
        var recipe = new Recipe { Key = "recipe_123", InternalName = "MakeSalsa", Name = "Make Salsa", Ingredients = [] };
        var (target, vm, _) = BuildTarget(recipe);

        var ok = target.TrySelectByInternalName("MakeSalsa");

        ok.Should().BeTrue();
        vm.SelectedRecipe.Should().Be(recipe);
    }

    [Fact]
    public void TrySelectByInternalName_UnknownRecipe_ReturnsFalse_VmUnchanged()
    {
        var (target, vm, _) = BuildTarget();
        target.TrySelectByInternalName("DoesNotExist").Should().BeFalse();
        vm.SelectedRecipe.Should().BeNull();
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
        // Direct link to a recipe must clear any leftover filter — otherwise the target
        // row might be filtered out of the visible list and the selection would land
        // on an invisible row. Reproduces the keyword-chip → direct-recipe-link sequence.
        var recipe = new Recipe { Key = "recipe_123", InternalName = "MakeSalsa", Name = "Make Salsa", Ingredients = [] };
        var (target, vm, _) = BuildTarget(recipe);
        vm.QueryText = "IngredientKeywords CONTAINS \"Crystal\"";

        var ok = target.TrySelectByInternalName("MakeSalsa");

        ok.Should().BeTrue();
        vm.QueryText.Should().BeEmpty(because: "the kind target clears any prior filter before selecting");
        vm.SelectedRecipe.Should().Be(recipe);
    }

    [Fact]
    public void TrySelectByInternalName_AfterRefresh_ResolvesFreshInstance()
    {
        // Simulates a background reference-data refresh: refData hands out a NEW
        // Recipe instance with the same InternalName, AND FileUpdated fires for
        // "recipes". The tab VM rebuilds AllRecipes via UiThread.Run (inline here
        // since Application.Current is null in tests) and the selection setter
        // resolves to the fresh instance — not the stale one captured at construction.
        var originalRecipe = new Recipe { Key = "recipe_123", InternalName = "MakeSalsa", Name = "Make Salsa", Ingredients = [] };
        var refData = new FakeReferenceData();
        refData.AddRecipe(originalRecipe);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new RecipesTabViewModel(refData, nav);
        var target = new RecipesKindTarget(vm);

        // Refresh: swap in a new Recipe instance.
        var refreshedRecipe = new Recipe { Key = "recipe_123", InternalName = "MakeSalsa", Name = "Make Salsa", Ingredients = [] };
        refData.AddRecipe(refreshedRecipe);
        refData.RaiseFileUpdated("recipes");

        target.TrySelectByInternalName("MakeSalsa").Should().BeTrue();
        vm.SelectedRow.Should().NotBeNull();
        vm.SelectedRecipe.Should().BeSameAs(refreshedRecipe);
    }

    [Fact]
    public void FileUpdated_PreservesCurrentSelectionByInternalName()
    {
        // VM has a selection. Refresh fires. New instance for the same name.
        // After refresh, selection is preserved (re-resolved to the new instance)
        // and detail VM is rebuilt with fresh data.
        var original = new Recipe { Key = "recipe_1", InternalName = "MakeSalsa", Name = "Make Salsa", Ingredients = [] };
        var refData = new FakeReferenceData();
        refData.AddRecipe(original);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new RecipesTabViewModel(refData, nav);
        vm.SelectedRow = vm.AllRecipes.Single();
        vm.SelectedRecipe.Should().BeSameAs(original);

        var refreshed = new Recipe { Key = "recipe_1", InternalName = "MakeSalsa", Name = "Make Salsa (v2)", Ingredients = [] };
        refData.AddRecipe(refreshed);
        refData.RaiseFileUpdated("recipes");

        vm.SelectedRecipe.Should().BeSameAs(refreshed);
        vm.DetailViewModel.Should().NotBeNull();
    }

    private static (RecipesKindTarget Target, RecipesTabViewModel Vm, FakeReferenceData RefData) BuildTarget(
        params Recipe[] recipes)
    {
        var refData = new FakeReferenceData();
        foreach (var recipe in recipes)
            refData.AddRecipe(recipe);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new RecipesTabViewModel(refData, nav);
        var target = new RecipesKindTarget(vm);
        return (target, vm, refData);
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, Recipe> _recipes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Recipe> _byInternalName = new(StringComparer.Ordinal);

        public void AddRecipe(Recipe recipe)
        {
            _recipes[recipe.Key] = recipe;
            if (recipe.InternalName is not null) _byInternalName[recipe.InternalName] = recipe;
        }

        public IReadOnlyList<string> Keys => Array.Empty<string>();
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes => _recipes;
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName => _byInternalName;
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
        public event EventHandler<string>? FileUpdated;
        public void RaiseFileUpdated(string fileKey) => FileUpdated?.Invoke(this, fileKey);
    }
}
