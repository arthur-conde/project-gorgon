using FluentAssertions;
using Mithril.Shared.Reference;
using Pippin.Domain;
using Xunit;

namespace Pippin.Tests;

public class FoodCatalogTests
{
    private static IReferenceDataService CreateRefData(params ItemEntry[] items)
    {
        var dict = new Dictionary<long, ItemEntry>();
        foreach (var item in items) dict[item.Id] = item;
        return new StubReferenceDataService(dict);
    }

    private static ItemEntry MakeFood(long id, string name, string foodDesc,
        Dictionary<string, int>? skillReqs = null, params string[] keywords)
    {
        var kws = keywords.Select(k =>
        {
            var eq = k.IndexOf('=');
            return eq > 0
                ? new ItemKeyword(k[..eq], int.Parse(k[(eq + 1)..]))
                : new ItemKeyword(k, 0);
        }).ToList();

        return new ItemEntry(id, name, name, 1, 0, kws,
            FoodDesc: foodDesc,
            SkillReqs: skillReqs);
    }

    [Fact]
    public void Builds_catalog_from_food_items()
    {
        var refData = CreateRefData(
            MakeFood(1, "Apple Juice", "Level 0 Instant Snack"),
            MakeFood(2, "Goblin Bread", "Level 20 Meal", new() { ["Gourmand"] = 5 }, "BreadDish=60", "VegetarianDish=60"));
        var catalog = new FoodCatalog(refData);

        catalog.TotalCount.Should().Be(2);
        catalog.ByName.Should().ContainKey("Apple Juice");
        catalog.ByName.Should().ContainKey("Goblin Bread");
    }

    [Fact]
    public void Parses_FoodDesc_level_and_type()
    {
        var refData = CreateRefData(MakeFood(1, "Test Meal", "Level 20 Meal"));
        var catalog = new FoodCatalog(refData);

        var entry = catalog.ByName["Test Meal"];
        entry.FoodLevel.Should().Be(20);
        entry.FoodType.Should().Be("Meal");
    }

    [Fact]
    public void Parses_instant_snack_type()
    {
        var refData = CreateRefData(MakeFood(1, "Sugar Water", "Level 0 Instant Snack"));
        var catalog = new FoodCatalog(refData);

        catalog.ByName["Sugar Water"].FoodType.Should().Be("Instant Snack");
    }

    [Fact]
    public void Extracts_gourmand_level_req()
    {
        var refData = CreateRefData(
            MakeFood(1, "Fancy Meal", "Level 31 Meal", new() { ["Gourmand"] = 16 }));
        var catalog = new FoodCatalog(refData);

        catalog.ByName["Fancy Meal"].GourmandLevelReq.Should().Be(16);
    }

    [Fact]
    public void Extracts_dietary_tags_from_keywords()
    {
        var refData = CreateRefData(
            MakeFood(1, "Veggie Dish", "Level 10 Meal", null,
                "VegetarianDish=60", "VeganDish=60"));
        var catalog = new FoodCatalog(refData);

        var tags = catalog.ByName["Veggie Dish"].DietaryTags;
        tags.Should().Contain("Vegetarian");
        tags.Should().Contain("Vegan");
    }

    [Fact]
    public void Excludes_non_food_items()
    {
        var nonFood = new ItemEntry(1, "Sword", "Sword", 1, 0, []);
        var food = MakeFood(2, "Bread", "Level 5 Meal");
        var catalog = new FoodCatalog(CreateRefData(nonFood, food));

        catalog.TotalCount.Should().Be(1);
        catalog.ByName.Should().NotContainKey("Sword");
    }

    private sealed class StubReferenceDataService : IReferenceDataService
    {
        public StubReferenceDataService(Dictionary<long, ItemEntry> items)
        {
            Items = items;
        }

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; }
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; } = new Dictionary<string, ItemEntry>();
        public ItemKeywordIndex KeywordIndex => new(Items);
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public event EventHandler<string>? FileUpdated;
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        private void SuppressWarning() => FileUpdated?.Invoke(this, "");
    }
}
