using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Crafting;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Crafting;

/// <summary>
/// Coverage for the shared demand-driven recipe expander (#226, supersedes #121).
/// Pins the contract the planner (#227) and Celebrimbor both rely on: producer
/// *alternatives*, multi-output crediting (the #42 fix), expected-value random
/// outputs, on-hand-aware shortfall, and cycle / depth safety.
/// </summary>
public class RecipeExpanderTests
{
    // ── RecipeProducerIndex ──────────────────────────────────────────────

    [Fact]
    public void ProducerIndex_KeepsAllAlternatives_InEnumerationOrder()
    {
        var data = new FakeRef()
            .AddItem(1, "Plank")
            .AddRecipe("SawPlank", produces: [(1, 4)], ingredients: [(2, 1)])
            .AddRecipe("MagicPlank", produces: [(1, 4)], ingredients: [(3, 1)]);

        var idx = RecipeProducerIndex.Build(data);

        idx.Alternatives("Plank").Select(r => r.InternalName)
            .Should().Equal("SawPlank", "MagicPlank");
        idx.TryGetDefault("Plank", out var first).Should().BeTrue();
        first.InternalName.Should().Be("SawPlank", because: "default = first in enumeration order");
        idx.HasProducer("Nope").Should().BeFalse();
    }

    // ── ExpectedYield ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 5, 5.0)]    // deterministic
    [InlineData(100.0, 5, 5.0)]   // 100% == deterministic
    [InlineData(50.0, 4, 2.0)]    // random → expected value
    [InlineData(0.0, 9, 0.0)]     // never drops
    public void ExpectedYield_DeterministicVsRandom(double? pct, int stack, double expected)
        => RecipeExpander.ExpectedYield(new RecipeResultItem { StackSize = stack, PercentChance = pct })
            .Should().Be(expected);

    // ── Single producer / single output (behaviour parity) ───────────────

    [Fact]
    public void Expand_PullsIngredientsForShortfall()
    {
        var data = new FakeRef()
            .AddItem(1, "Bread").AddItem(2, "Flour").AddItem(3, "Water")
            .AddRecipe("BakeBread", produces: [(1, 1)], ingredients: [(2, 2), (3, 1)]);

        var demand = new Dictionary<string, double> { ["Bread"] = 3 };
        var expander = new RecipeExpander(data);
        expander.Expand(demand, maxDepth: 1, null, null, new Dictionary<string, KeywordSlot>());

        demand["Flour"].Should().Be(6);
        demand["Water"].Should().Be(3);
        demand["Bread"].Should().Be(3, because: "intermediates stay in the map for the caller to render");
    }

    [Fact]
    public void Expand_OnHandReducesShortfall()
    {
        var data = new FakeRef()
            .AddItem(1, "Bread").AddItem(2, "Flour")
            .AddRecipe("BakeBread", produces: [(1, 1)], ingredients: [(2, 2)]);

        var demand = new Dictionary<string, double> { ["Bread"] = 10 };
        new RecipeExpander(data).Expand(
            demand, maxDepth: 1,
            onHandByInternalName: new Dictionary<string, int> { ["Bread"] = 4 },
            overridesByInternalName: null,
            new Dictionary<string, KeywordSlot>());

        demand["Flour"].Should().Be(12, because: "only the shortfall of 6 needs crafting → 6×2 flour");
    }

    // ── #42: multi-output sibling crediting ──────────────────────────────

    [Fact]
    public void Expand_MultiOutputRecipe_CreditsSiblingDemand_Issue42()
    {
        // Smelting yields BOTH an Ingot and Slag per batch. The plan independently
        // wants Slag too. Before #42 the Slag demand was crafted again from a Slag
        // recipe; now the Ingot batches' Slag byproduct is credited first.
        var data = new FakeRef()
            .AddItem(1, "Ingot").AddItem(2, "Slag").AddItem(3, "Ore")
            .AddRecipe("Smelt", produces: [(1, 1), (2, 1)], ingredients: [(3, 2)]);

        var demand = new Dictionary<string, double> { ["Ingot"] = 10, ["Slag"] = 4 };
        new RecipeExpander(data).Expand(
            demand, maxDepth: 1, null, null, new Dictionary<string, KeywordSlot>());

        demand["Ore"].Should().Be(20, because: "10 ingots → 10 batches → 20 ore (NOT re-crafted for slag)");
        demand.GetValueOrDefault("Slag", 0).Should().Be(0,
            because: "10 batches also yield 10 slag, fully covering the demand of 4 — "
                     + "the byproduct is credited, not re-crafted, then the zeroed row is pruned (#42)");
    }

    [Fact]
    public void Expand_MultiOutput_RandomSiblingCreditedAtExpectedValue()
    {
        var data = new FakeRef()
            .AddItem(1, "Gem").AddItem(2, "Dust").AddItem(3, "Rock")
            .AddRecipe("Crack", produces: [(1, 1)], randomProduces: [(2, 2, 50.0)], ingredients: [(3, 1)]);

        // 8 gems → 8 batches; Dust expected = 8 × (2 × 0.5) = 8.
        var demand = new Dictionary<string, double> { ["Gem"] = 8, ["Dust"] = 30 };
        new RecipeExpander(data).Expand(
            demand, maxDepth: 1, null, null, new Dictionary<string, KeywordSlot>());

        demand["Dust"].Should().Be(22, because: "30 wanted − 8 expected byproduct");
    }

    // ── Choice policy ────────────────────────────────────────────────────

    [Fact]
    public void Expand_HonoursChoicePolicy()
    {
        var data = new FakeRef()
            .AddItem(1, "Plank").AddItem(2, "Log").AddItem(3, "Mana")
            .AddRecipe("SawPlank", produces: [(1, 1)], ingredients: [(2, 3)])
            .AddRecipe("ConjurePlank", produces: [(1, 1)], ingredients: [(3, 1)]);

        var demand = new Dictionary<string, double> { ["Plank"] = 5 };
        new RecipeExpander(data).Expand(
            demand, maxDepth: 1, null, null, new Dictionary<string, KeywordSlot>(),
            choose: (_, alts) => alts.Single(r => r.InternalName == "ConjurePlank"));

        demand.Should().ContainKey("Mana").WhoseValue.Should().Be(5);
        demand.Should().NotContainKey("Log", because: "the policy picked the conjure recipe");
    }

    // ── Cycle / depth safety ─────────────────────────────────────────────

    [Fact]
    public void Expand_CycleSafe()
    {
        // A ← needs B; B ← needs A. Must terminate.
        var data = new FakeRef()
            .AddItem(1, "A").AddItem(2, "B")
            .AddRecipe("MakeA", produces: [(1, 1)], ingredients: [(2, 1)])
            .AddRecipe("MakeB", produces: [(2, 1)], ingredients: [(1, 1)]);

        var demand = new Dictionary<string, double> { ["A"] = 1 };
        var act = () => new RecipeExpander(data).Expand(
            demand, maxDepth: 16, null, null, new Dictionary<string, KeywordSlot>());

        act.Should().NotThrow();
        demand.Should().ContainKey("B");
    }

    [Fact]
    public void Expand_DepthCapBoundsRecursion()
    {
        // C → B → A. depth 1 only pulls the first level.
        var data = new FakeRef()
            .AddItem(1, "A").AddItem(2, "B").AddItem(3, "C").AddItem(4, "Raw")
            .AddRecipe("MakeC", produces: [(3, 1)], ingredients: [(2, 1)])
            .AddRecipe("MakeB", produces: [(2, 1)], ingredients: [(1, 1)])
            .AddRecipe("MakeA", produces: [(1, 1)], ingredients: [(4, 1)]);

        var demand = new Dictionary<string, double> { ["C"] = 1 };
        new RecipeExpander(data).Expand(
            demand, maxDepth: 1, null, null, new Dictionary<string, KeywordSlot>());

        demand.Should().ContainKey("B");
        demand.Should().NotContainKey("A", because: "depth 1 stops after C's direct ingredients");
    }

    // ── Minimal IReferenceDataService fake ───────────────────────────────

    private sealed class FakeRef : IReferenceDataService
    {
        private readonly Dictionary<long, Item> _items = new();
        private readonly Dictionary<string, Item> _itemsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Recipe> _recipes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Recipe> _recipesByName = new(StringComparer.Ordinal);
        private int _serial;

        public FakeRef AddItem(long id, string internalName)
        {
            var item = new Item { Id = id, Name = internalName, InternalName = internalName };
            _items[id] = item;
            _itemsByName[internalName] = item;
            return this;
        }

        public FakeRef AddRecipe(
            string internalName,
            (long id, int stack)[] produces,
            (long id, int stack)[]? ingredients = null,
            (long id, int stack, double pct)[]? randomProduces = null)
        {
            var results = produces
                .Select(p => new RecipeResultItem { ItemCode = p.id, StackSize = p.stack })
                .Concat((randomProduces ?? [])
                    .Select(p => new RecipeResultItem { ItemCode = p.id, StackSize = p.stack, PercentChance = p.pct }))
                .ToList();
            var recipe = new Recipe
            {
                Key = $"recipe_{++_serial}",
                Name = internalName,
                InternalName = internalName,
                Ingredients = (ingredients ?? [])
                    .Select(i => (RecipeIngredient)new RecipeItemIngredient { ItemCode = i.id, StackSize = i.stack })
                    .ToList(),
                ResultItems = results,
            };
            _recipes[recipe.Key] = recipe;
            _recipesByName[internalName] = recipe;
            return this;
        }

        public IReadOnlyList<string> Keys { get; } = ["items", "recipes"];
        public IReadOnlyDictionary<long, Item> Items => _items;
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => _itemsByName;
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes => _recipes;
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName => _recipesByName;
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Quest> Quests { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
