using FluentAssertions;
using Mithril.Shared.Reference;
using Pippin.Domain;
using Pippin.Parsing;
using Pippin.State;
using Xunit;

namespace Pippin.Tests;

public class GourmandStateMachineTests
{
    private static FoodCatalog CreateEmptyCatalog()
    {
        // Create a minimal mock reference data service
        var refData = new StubReferenceDataService();
        return new FoodCatalog(refData);
    }

    private static FoodsConsumedReport MakeReport(params (string Name, int Count)[] foods)
    {
        var entries = foods.Select(f =>
            new FoodConsumedEntry(f.Name, f.Count, Array.Empty<string>())).ToList();
        return new FoodsConsumedReport(DateTime.UtcNow, entries);
    }

    [Fact]
    public void Apply_populates_eaten_foods()
    {
        var sm = new GourmandStateMachine(CreateEmptyCatalog());
        sm.Apply(MakeReport(("Apple Juice", 5), ("Bacon", 2)));

        sm.EatenFoods.Should().HaveCount(2);
        sm.EatenFoods["Apple Juice"].Should().Be(5);
        sm.EatenFoods["Bacon"].Should().Be(2);
        sm.HasData.Should().BeTrue();
    }

    [Fact]
    public void Second_report_replaces_first()
    {
        var sm = new GourmandStateMachine(CreateEmptyCatalog());
        sm.Apply(MakeReport(("Apple Juice", 5), ("Bacon", 2)));
        sm.Apply(MakeReport(("Grapes", 3)));

        sm.EatenFoods.Should().HaveCount(1);
        sm.EatenFoods.Should().ContainKey("Grapes");
        sm.EatenFoods.Should().NotContainKey("Apple Juice");
    }

    [Fact]
    public void Hydrate_restores_state_without_firing_events()
    {
        var sm = new GourmandStateMachine(CreateEmptyCatalog());
        var eventFired = false;
        sm.StateChanged += (_, _) => eventFired = true;

        sm.Hydrate(new GourmandState
        {
            EatenFoods = new Dictionary<string, int> { ["Bacon"] = 1 },
            LastReportTime = DateTimeOffset.UtcNow,
        });

        eventFired.Should().BeFalse();
        sm.EatenFoods.Should().ContainKey("Bacon");
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

    /// <summary>Minimal stub so FoodCatalog can be constructed without real CDN data.</summary>
    private sealed class StubReferenceDataService : IReferenceDataService
    {
        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; } = new Dictionary<long, ItemEntry>();
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; } = new Dictionary<string, ItemEntry>();
        public ItemKeywordIndex KeywordIndex { get; } = ItemKeywordIndex.Empty;
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
        private void SuppressWarning() => FileUpdated?.Invoke(this, ""); // suppress CS0067
    }
}
