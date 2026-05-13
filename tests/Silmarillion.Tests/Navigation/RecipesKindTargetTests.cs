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

    private static (RecipesKindTarget Target, RecipesTabViewModel Vm, FakeReferenceData RefData) BuildTarget(
        params Recipe[] recipes)
    {
        var refData = new FakeReferenceData();
        foreach (var recipe in recipes)
            refData.AddRecipe(recipe);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new RecipesTabViewModel(refData, nav);
        var target = new RecipesKindTarget(vm, refData);
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
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
