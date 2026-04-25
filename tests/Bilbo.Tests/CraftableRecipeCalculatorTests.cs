using Bilbo.Domain;
using FluentAssertions;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
using Xunit;

namespace Bilbo.Tests;

public class CraftableRecipeCalculatorTests
{
    private static StorageItemRow Row(int typeId, int stack, string name = "item", string location = "Inventory") =>
        new(name, location, stack, 0, 0, null, null, null, 0, null, false, typeId, 0, name);

    private static ItemEntry Item(long id, string name) => new(id, name, name, 100, 0, []);

    private static RecipeEntry Recipe(string key, string name, IReadOnlyList<RecipeItemRef> ingredients, IReadOnlyList<RecipeItemRef>? results = null, string skill = "Cooking", int skillReq = 0, string internalName = "r_default") =>
        new(
            Key: key, Name: name, InternalName: internalName, IconId: 0,
            Skill: skill, SkillLevelReq: skillReq,
            RewardSkill: "", RewardSkillXp: 0, RewardSkillXpFirstTime: 0,
            RewardSkillXpDropOffLevel: null, RewardSkillXpDropOffPct: null, RewardSkillXpDropOffRate: null,
            Ingredients: ingredients,
            ResultItems: results ?? []);

    [Fact]
    public void Happy_Path_Computes_Max_Craftable()
    {
        var refData = new TestRefData(
            items: new()
            {
                [100] = Item(100, "Onion"),
                [200] = Item(200, "Salt"),
                [900] = Item(900, "Stew"),
            },
            recipes: new()
            {
                ["recipe_stew"] = Recipe("recipe_stew", "Stew",
                    ingredients:
                    [
                        new(100, 2, null),
                        new(200, 1, null),
                    ],
                    results: [new(900, 1, null)]),
            });

        var rows = CraftableRecipeCalculator.Compute(
            [Row(100, 4, "Onion"), Row(200, 2, "Salt")],
            refData, character: null, ConfidenceLevel.P95);

        rows.Should().HaveCount(1);
        var row = rows[0];
        row.MaxCraftable.Should().Be(2); // onion 4/2 = 2, salt 2/1 = 2 → min 2
        row.MissingIngredients.Should().BeEmpty();
        row.Ingredients.Should().Be("Onion x2, Salt x1");
        row.ResultItem.Should().Be("Stew");
        row.ResultStackSize.Should().Be(1);
    }

    [Fact]
    public void Missing_Ingredient_Reports_Shortfall()
    {
        var refData = new TestRefData(
            items: new()
            {
                [100] = Item(100, "Onion"),
                [300] = Item(300, "Pepper"),
            },
            recipes: new()
            {
                ["recipe_peppered_onion"] = Recipe("recipe_peppered_onion", "Peppered Onion",
                    ingredients: [new(100, 2, null), new(300, 1, null)]),
            });

        var rows = CraftableRecipeCalculator.Compute(
            [Row(100, 4, "Onion")], // no pepper at all
            refData, character: null, ConfidenceLevel.P95);

        var row = rows.Single();
        row.MaxCraftable.Should().Be(0);
        row.MissingIngredients.Should().Be("Pepper x1 (have 0)");
    }

    [Fact]
    public void Multi_Vault_Aggregation_Sums_By_TypeID()
    {
        var refData = new TestRefData(
            items: new() { [100] = Item(100, "Onion") },
            recipes: new()
            {
                ["r"] = Recipe("r", "OnionSoup", ingredients: [new(100, 1, null)]),
            });

        var rows = CraftableRecipeCalculator.Compute(
            [
                Row(100, 2, "Onion", "Inventory"),
                Row(100, 3, "Onion", "NPC: Baker"),
            ],
            refData, character: null, ConfidenceLevel.WorstCase);

        rows.Single().MaxCraftable.Should().Be(5);
    }

    [Fact]
    public void Unknown_ItemCode_Falls_Back_To_Hash_Name()
    {
        var refData = new TestRefData(
            items: new(), // empty — no lookup succeeds
            recipes: new()
            {
                ["r"] = Recipe("r", "Mystery", ingredients: [new(12345, 1, null)]),
            });

        var rows = CraftableRecipeCalculator.Compute([], refData, character: null, ConfidenceLevel.P95);
        var row = rows.Single();
        row.Ingredients.Should().Be("#12345 x1");
        row.MaxCraftable.Should().Be(0);
        row.MissingIngredients.Should().Be("#12345 x1 (have 0)");
    }

    [Fact]
    public void Character_Decoration_Populates_Skill_And_Known()
    {
        var refData = new TestRefData(
            items: new() { [100] = Item(100, "Onion") },
            recipes: new()
            {
                ["r"] = Recipe("r", "Stew", ingredients: [new(100, 1, null)],
                    skill: "Cooking", skillReq: 30, internalName: "recipe_stew"),
            });

        var character = new CharacterSnapshot(
            "Alice", "Alpha", DateTimeOffset.UtcNow,
            Skills: new Dictionary<string, CharacterSkill>
            {
                ["Cooking"] = new CharacterSkill(Level: 30, BonusLevels: 5, XpTowardNextLevel: 0, XpNeededForNextLevel: 0),
            },
            RecipeCompletions: new Dictionary<string, int> { ["recipe_stew"] = 2 },
            NpcFavor: new Dictionary<string, string>());

        var rows = CraftableRecipeCalculator.Compute([Row(100, 10)], refData, character, ConfidenceLevel.P95);
        var row = rows.Single();
        row.CharacterSkillLevel.Should().Be(35);
        row.SkillLevelMet.Should().BeTrue();
        row.IsKnown.Should().BeTrue();
        row.TimesCompleted.Should().Be(2);
    }

    [Fact]
    public void Null_Character_Leaves_Decoration_Defaulted()
    {
        var refData = new TestRefData(
            items: new() { [100] = Item(100, "Onion") },
            recipes: new()
            {
                ["r"] = Recipe("r", "Stew", ingredients: [new(100, 1, null)],
                    skill: "Cooking", skillReq: 30),
            });

        var row = CraftableRecipeCalculator
            .Compute([Row(100, 10)], refData, character: null, ConfidenceLevel.P95)
            .Single();

        row.CharacterSkillLevel.Should().BeNull();
        row.SkillLevelMet.Should().BeFalse();
        row.IsKnown.Should().BeFalse();
        row.TimesCompleted.Should().Be(0);
        row.MaxCraftable.Should().Be(10); // storage math still works
    }

    [Fact]
    public void Catalyst_Ingredient_Shows_Probability_In_Display()
    {
        var refData = new TestRefData(
            items: new()
            {
                [100] = Item(100, "Onion"),
                [500] = Item(500, "Catalyst"),
            },
            recipes: new()
            {
                ["r"] = Recipe("r", "Stew",
                    ingredients: [new(100, 1, null), new(500, 1, 0.5f)]),
            });

        var row = CraftableRecipeCalculator
            .Compute([Row(100, 10), Row(500, 10)], refData, null, ConfidenceLevel.P95)
            .Single();

        row.Ingredients.Should().Contain("Catalyst x1 (p=50%)");
    }

    private sealed class TestRefData : IReferenceDataService
    {
        public TestRefData(Dictionary<long, ItemEntry> items, Dictionary<string, RecipeEntry> recipes)
        {
            Items = items;
            Recipes = recipes;
        }

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; }
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; } = new Dictionary<string, ItemEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; }
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "v469", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
