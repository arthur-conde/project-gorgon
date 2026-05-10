using FluentAssertions;
using Mithril.Shared.Reference;
using Pippin.Domain;
using Pippin.Parsing;
using Pippin.State;
using Xunit;

namespace Pippin.Tests;

public class GourmandStateMachineTests
{
    private static FoodCatalog CreateEmptyCatalog() => new(new StubReferenceDataService([]));

    private static FoodCatalog CreateCatalog(params (long Id, string InternalName, string Name)[] foods)
    {
        var dict = new Dictionary<long, ItemEntry>();
        foreach (var (id, internalName, name) in foods)
            dict[id] = new ItemEntry(id, name, internalName, 1, 0, [], FoodDesc: "Level 0 Snack");
        return new FoodCatalog(new StubReferenceDataService(dict));
    }

    private static FoodsConsumedReport MakeReport(params (string Name, int Count)[] foods)
    {
        var entries = foods.Select(f =>
            new FoodConsumedEntry(f.Name, f.Count, Array.Empty<string>())).ToList();
        return new FoodsConsumedReport(DateTime.UtcNow, entries);
    }

    [Fact]
    public void Apply_resolves_known_foods_to_internal_name()
    {
        var sm = new GourmandStateMachine(CreateCatalog(
            (1, "FoodAppleJuice", "Apple Juice"),
            (2, "FoodBacon", "Bacon")));

        sm.Apply(MakeReport(("Apple Juice", 5), ("Bacon", 2)));

        sm.EatenFoodsByInternalName.Should().HaveCount(2);
        sm.EatenFoodsByInternalName["FoodAppleJuice"].Should().Be(5);
        sm.EatenFoodsByInternalName["FoodBacon"].Should().Be(2);
        sm.UnknownByName.Should().BeEmpty();
        sm.HasData.Should().BeTrue();
    }

    [Fact]
    public void Apply_with_unknown_food_buckets_into_UnknownByName()
    {
        var sm = new GourmandStateMachine(CreateCatalog((1, "FoodBacon", "Bacon")));

        sm.Apply(MakeReport(("Bacon", 2), ("Mystery Stew", 1)));

        sm.EatenFoodsByInternalName.Should().ContainKey("FoodBacon").WhoseValue.Should().Be(2);
        sm.UnknownByName.Should().ContainKey("Mystery Stew").WhoseValue.Should().Be(1);
        sm.EatenCount.Should().Be(2, "unique foods include both resolved and unknown buckets");
    }

    [Fact]
    public void Second_report_replaces_first()
    {
        var sm = new GourmandStateMachine(CreateCatalog(
            (1, "FoodAppleJuice", "Apple Juice"),
            (2, "FoodBacon", "Bacon"),
            (3, "FoodGrapes", "Grapes")));

        sm.Apply(MakeReport(("Apple Juice", 5), ("Bacon", 2)));
        sm.Apply(MakeReport(("Grapes", 3)));

        sm.EatenFoodsByInternalName.Should().HaveCount(1);
        sm.EatenFoodsByInternalName.Should().ContainKey("FoodGrapes");
        sm.EatenFoodsByInternalName.Should().NotContainKey("FoodAppleJuice");
    }

    [Fact]
    public void Hydrate_restores_state_without_firing_events()
    {
        var sm = new GourmandStateMachine(CreateEmptyCatalog());
        var eventFired = false;
        sm.StateChanged += (_, _) => eventFired = true;

        sm.Hydrate(new GourmandState
        {
            EatenFoodsByInternalName = new Dictionary<string, int>(StringComparer.Ordinal) { ["FoodBacon"] = 1 },
            UnknownByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Mystery Stew"] = 4 },
            LastReportTime = DateTimeOffset.UtcNow,
        });

        eventFired.Should().BeFalse();
        sm.EatenFoodsByInternalName.Should().ContainKey("FoodBacon");
        sm.UnknownByName.Should().ContainKey("Mystery Stew");
    }

    [Fact]
    public void StateChanged_fires_on_Apply()
    {
        var sm = new GourmandStateMachine(CreateEmptyCatalog());
        var eventFired = false;
        sm.StateChanged += (_, _) => eventFired = true;

        sm.Apply(MakeReport(("Apple Juice", 1)));

        eventFired.Should().BeTrue();
    }

    [Fact]
    public void ApplyLegacyByName_resolves_through_catalog()
    {
        var sm = new GourmandStateMachine(CreateCatalog(
            (1, "FoodAppleJuice", "Apple Juice"),
            (2, "FoodBacon", "Bacon")));

        var changed = sm.ApplyLegacyByName(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Apple Juice"] = 8,
            ["Bacon"] = 2,
            ["Mystery Stew"] = 1, // not in catalog → unknown
        });

        changed.Should().BeTrue();
        sm.EatenFoodsByInternalName["FoodAppleJuice"].Should().Be(8);
        sm.EatenFoodsByInternalName["FoodBacon"].Should().Be(2);
        sm.UnknownByName.Should().ContainKey("Mystery Stew").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void ReconcileUnknowns_promotes_entries_when_catalog_catches_up()
    {
        var stub = new StubReferenceDataService(new Dictionary<long, ItemEntry>
        {
            [1] = new(1, "Bacon", "FoodBacon", 1, 0, [], FoodDesc: "Level 0 Snack"),
        });
        var catalog = new FoodCatalog(stub);
        var sm = new GourmandStateMachine(catalog);

        // Sender ate Mystery Stew before our CDN snapshot knew about it.
        sm.Hydrate(new GourmandState
        {
            UnknownByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Mystery Stew"] = 4 },
        });
        sm.UnknownByName.Should().HaveCount(1);

        // CDN refresh adds the food to the catalog and rebuilds.
        stub.Add(new ItemEntry(2, "Mystery Stew", "FoodMysteryStew", 1, 0, [], FoodDesc: "Level 5 Meal"));
        stub.RaiseFileUpdated();

        sm.ReconcileUnknowns().Should().BeTrue();
        sm.UnknownByName.Should().BeEmpty();
        sm.EatenFoodsByInternalName.Should().ContainKey("FoodMysteryStew").WhoseValue.Should().Be(4);
    }

    /// <summary>Minimal stub so FoodCatalog can be constructed with controllable contents.</summary>
    private sealed class StubReferenceDataService : IReferenceDataService
    {
        private readonly Dictionary<long, ItemEntry> _items;

        public StubReferenceDataService(Dictionary<long, ItemEntry> items)
        {
            _items = items;
        }

        public void Add(ItemEntry item) => _items[item.Id] = item;
        public void RaiseFileUpdated() => FileUpdated?.Invoke(this, "items");

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, ItemEntry> Items => _items;
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; } = new Dictionary<string, ItemEntry>();
        public ItemKeywordIndex KeywordIndex => ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, QuestEntry> Quests { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public event EventHandler<string>? FileUpdated;
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
    }
}
