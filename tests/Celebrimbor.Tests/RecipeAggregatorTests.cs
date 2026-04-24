using Celebrimbor.Domain;
using Celebrimbor.Services;
using FluentAssertions;
using Gorgon.Shared.Reference;
using Xunit;

namespace Celebrimbor.Tests;

public class RecipeAggregatorTests
{
    private static RecipeItemRef Ref(long id, int stack, float? chance = null)
        => new(id, stack, chance);

    private static FakeReferenceData MakeStandardData()
    {
        var items = new[]
        {
            FakeReferenceData.Item(10, "Milk", "CookingIngredient"),
            FakeReferenceData.Item(11, "Butter", "CookingIngredient"),
            FakeReferenceData.Item(12, "Salt", "CookingIngredient"),
            FakeReferenceData.Item(13, "Bread", "CookingDish"),
        };
        var recipes = new[]
        {
            FakeReferenceData.Recipe("Butter", "Cheesemaking", 0,
                ingredients: [Ref(10, 2), Ref(12, 1)],
                results: [Ref(11, 1)]),
            FakeReferenceData.Recipe("Bread", "Cooking", 5,
                ingredients: [Ref(11, 1), Ref(12, 1)],
                results: [Ref(13, 1)]),
        };
        return new FakeReferenceData(items, recipes);
    }

    [Fact]
    public void Target_appears_as_row_at_expansion_depth_zero()
    {
        var data = MakeStandardData();
        var sut = new RecipeAggregator();

        var entries = new[] { new CraftListEntry { RecipeInternalName = "Butter", Quantity = 3 } };
        var result = sut.Aggregate(entries, expansionDepth: 0, data);

        // At depth 0, only the target shows as a row — ingredients don't appear.
        result.Should().ContainSingle(r => r.ItemInternalName == "Butter")
            .Which.TotalNeeded.Should().Be(3);
        result.Should().NotContain(r => r.ItemInternalName == "Milk");
        result.Should().NotContain(r => r.ItemInternalName == "Salt");
    }

    [Fact]
    public void Target_and_direct_ingredients_scale_linearly_at_depth_one()
    {
        var data = MakeStandardData();
        var sut = new RecipeAggregator();

        var entries = new[] { new CraftListEntry { RecipeInternalName = "Butter", Quantity = 3 } };
        var result = sut.Aggregate(entries, expansionDepth: 1, data);

        result.Should().ContainSingle(r => r.ItemInternalName == "Butter")
            .Which.TotalNeeded.Should().Be(3);
        result.Should().ContainSingle(r => r.ItemInternalName == "Milk")
            .Which.TotalNeeded.Should().Be(6);
        result.Should().ContainSingle(r => r.ItemInternalName == "Salt")
            .Which.TotalNeeded.Should().Be(3);
    }

    [Fact]
    public void Shared_ingredient_across_recipes_sums()
    {
        var data = MakeStandardData();
        var sut = new RecipeAggregator();

        var entries = new[]
        {
            new CraftListEntry { RecipeInternalName = "Butter", Quantity = 2 },
            new CraftListEntry { RecipeInternalName = "Bread", Quantity = 4 },
        };
        // depth 1 = targets + direct ingredients. Bread's ingredient Butter collides with the
        // Butter target, so the Butter row carries the combined demand. Both targets expand
        // at level 0, so Salt accrues from both paths.
        var result = sut.Aggregate(entries, expansionDepth: 1, data);

        result.Should().ContainSingle(r => r.ItemInternalName == "Butter")
            .Which.TotalNeeded.Should().Be(6); // 2 direct + 4 as bread ingredient
        result.Should().ContainSingle(r => r.ItemInternalName == "Salt")
            .Which.TotalNeeded.Should().Be(6); // 2 from butter expansion + 4 from bread expansion
    }

    [Fact]
    public void ChanceToConsume_halves_expected_demand()
    {
        var items = new[]
        {
            FakeReferenceData.Item(10, "Milk"),
            FakeReferenceData.Item(11, "Butter"),
        };
        var recipes = new[]
        {
            FakeReferenceData.Recipe("Butter", "Cheesemaking", 0,
                ingredients: [Ref(10, 2, chance: 0.5f)],
                results: [Ref(11, 1)]),
        };
        var data = new FakeReferenceData(items, recipes);
        var sut = new RecipeAggregator();

        var entries = new[] { new CraftListEntry { RecipeInternalName = "Butter", Quantity = 3 } };
        var result = sut.Aggregate(entries, expansionDepth: 1, data);

        var milk = result.Single(r => r.ItemInternalName == "Milk");
        milk.ExpectedNeeded.Should().BeApproximately(3.0, 1e-6); // 3 batches * 2 * 0.5
        milk.TotalNeeded.Should().Be(3); // ceil(3)
    }

    [Fact]
    public void ExpansionDepth_zero_shows_only_targets()
    {
        var data = MakeStandardData();
        var sut = new RecipeAggregator();

        var entries = new[] { new CraftListEntry { RecipeInternalName = "Bread", Quantity = 2 } };
        var result = sut.Aggregate(entries, expansionDepth: 0, data);

        result.Should().ContainSingle(r => r.ItemInternalName == "Bread")
            .Which.TotalNeeded.Should().Be(2);
        result.Should().NotContain(r => r.ItemInternalName == "Butter");
        result.Should().NotContain(r => r.ItemInternalName == "Milk");
    }

    [Fact]
    public void ExpansionDepth_two_expands_full_chain()
    {
        var data = MakeStandardData();
        var sut = new RecipeAggregator();

        var entries = new[] { new CraftListEntry { RecipeInternalName = "Bread", Quantity = 2 } };
        var result = sut.Aggregate(entries, expansionDepth: 2, data);

        // Targets and intermediates all visible; ingredients reach down to raw materials.
        result.Should().ContainSingle(r => r.ItemInternalName == "Bread")
            .Which.TotalNeeded.Should().Be(2);
        result.Should().ContainSingle(r => r.ItemInternalName == "Butter")
            .Which.TotalNeeded.Should().Be(2);
        result.Should().ContainSingle(r => r.ItemInternalName == "Milk")
            .Which.TotalNeeded.Should().Be(4);
        // Salt: 2 from bread + 2 from butter = 4
        result.Should().ContainSingle(r => r.ItemInternalName == "Salt")
            .Which.TotalNeeded.Should().Be(4);
    }

    [Fact]
    public void Expansion_respects_manual_override_on_intermediate()
    {
        var data = MakeStandardData();
        var sut = new RecipeAggregator();

        // No detected on-hand — but the user manually overrides butter to 1.
        // That should cut the raw-ingredient shortfall the same way real stock would.
        var overrides = new Dictionary<string, int>(StringComparer.Ordinal) { ["Butter"] = 1 };
        var entries = new[] { new CraftListEntry { RecipeInternalName = "Bread", Quantity = 2 } };
        var result = sut.Aggregate(entries, expansionDepth: 2, data, null, null, overrides);

        result.Should().ContainSingle(r => r.ItemInternalName == "Butter")
            .Which.OnHandOverride.Should().Be(1);
        // Only 1 butter-batch's worth of raw ingredients needed (2 milk, 1 salt)
        // plus the 2 salt from the bread recipe → 3 salt total.
        result.Should().ContainSingle(r => r.ItemInternalName == "Milk")
            .Which.TotalNeeded.Should().Be(2);
        result.Should().ContainSingle(r => r.ItemInternalName == "Salt")
            .Which.TotalNeeded.Should().Be(3);
    }

    [Fact]
    public void Expansion_uses_on_hand_stock_to_reduce_ingredient_shortfall()
    {
        var data = MakeStandardData();
        var sut = new RecipeAggregator();

        // User wants 2 bread. They already have 1 butter on hand, so only 1 more butter needs to be crafted,
        // which needs just 2 milk and 1 salt (plus the 2 salt from the bread itself → 3 total).
        var onHand = new Dictionary<string, int>(StringComparer.Ordinal) { ["Butter"] = 1 };
        var entries = new[] { new CraftListEntry { RecipeInternalName = "Bread", Quantity = 2 } };
        var result = sut.Aggregate(entries, expansionDepth: 2, data, onHand);

        result.Should().ContainSingle(r => r.ItemInternalName == "Butter")
            .Which.OnHandDetected.Should().Be(1);
        result.Should().ContainSingle(r => r.ItemInternalName == "Milk")
            .Which.TotalNeeded.Should().Be(2);
        result.Should().ContainSingle(r => r.ItemInternalName == "Salt")
            .Which.TotalNeeded.Should().Be(3);
    }

    [Fact]
    public void ExpansionDepth_handles_cycles_without_looping()
    {
        // Cycle: A needs B; B needs A.
        var items = new[]
        {
            FakeReferenceData.Item(20, "A"),
            FakeReferenceData.Item(21, "B"),
            FakeReferenceData.Item(22, "RawC"),
        };
        var recipes = new[]
        {
            FakeReferenceData.Recipe("A", "Test", 0,
                ingredients: [Ref(21, 1), Ref(22, 1)],
                results: [Ref(20, 1)]),
            FakeReferenceData.Recipe("B", "Test", 0,
                ingredients: [Ref(20, 1), Ref(22, 1)],
                results: [Ref(21, 1)]),
        };
        var data = new FakeReferenceData(items, recipes);
        var sut = new RecipeAggregator();

        var entries = new[] { new CraftListEntry { RecipeInternalName = "A", Quantity = 1 } };

        // Should terminate — not stack overflow — regardless of depth.
        var act = () => sut.Aggregate(entries, expansionDepth: 5, data);
        act.Should().NotThrow();
    }

    [Fact]
    public void Missing_ingredient_metadata_is_skipped_not_crashed()
    {
        var items = new[]
        {
            FakeReferenceData.Item(10, "Milk"),
            FakeReferenceData.Item(11, "Butter"),
        };
        // Recipe references an ingredient item we don't have (id 99). Aggregator should skip it
        // when expanding, but still produce the target row and the resolvable ingredient.
        var recipes = new[]
        {
            FakeReferenceData.Recipe("Butter", "Test", 0,
                ingredients: [Ref(10, 2), Ref(99, 1)],
                results: [Ref(11, 1)]),
        };
        var data = new FakeReferenceData(items, recipes);
        var sut = new RecipeAggregator();

        var entries = new[] { new CraftListEntry { RecipeInternalName = "Butter", Quantity = 1 } };
        var result = sut.Aggregate(entries, expansionDepth: 1, data);

        result.Should().Contain(r => r.ItemInternalName == "Butter");
        result.Should().Contain(r => r.ItemInternalName == "Milk");
    }

    [Fact]
    public void Empty_keywords_fall_back_to_misc_tag()
    {
        var items = new[] { FakeReferenceData.Item(10, "Unlabelled") };
        var recipes = new[]
        {
            FakeReferenceData.Recipe("UseIt", "Test", 0,
                ingredients: [Ref(10, 1)],
                results: [Ref(10, 1)]),
        };
        var data = new FakeReferenceData(items, recipes);
        var sut = new RecipeAggregator();

        var entries = new[] { new CraftListEntry { RecipeInternalName = "UseIt", Quantity = 1 } };
        var result = sut.Aggregate(entries, expansionDepth: 0, data);

        result.Should().ContainSingle().Which.PrimaryTag.Should().Be("Misc");
    }

    [Fact]
    public void Zero_or_negative_quantity_entries_are_ignored()
    {
        var data = MakeStandardData();
        var sut = new RecipeAggregator();

        var entries = new[]
        {
            new CraftListEntry { RecipeInternalName = "Butter", Quantity = 0 },
            new CraftListEntry { RecipeInternalName = "Butter", Quantity = -5 },
        };
        var result = sut.Aggregate(entries, expansionDepth: 0, data);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_recipe_internal_name_is_ignored()
    {
        var data = MakeStandardData();
        var sut = new RecipeAggregator();

        var entries = new[] { new CraftListEntry { RecipeInternalName = "DoesNotExist", Quantity = 1 } };
        var result = sut.Aggregate(entries, expansionDepth: 0, data);

        result.Should().BeEmpty();
    }

    [Fact]
    public void TargetFullyOverridden_KeepsChainVisible()
    {
        var data = MakeStandardData();
        var sut = new RecipeAggregator();

        // User wants 2 bread and tells us they already have 2 on hand. Shortfall collapses to zero,
        // but the ingredient chain should still be visible as 0/0 complete rows — the plan shouldn't
        // evaporate, it should visibly complete.
        var overrides = new Dictionary<string, int>(StringComparer.Ordinal) { ["Bread"] = 2 };
        var entries = new[] { new CraftListEntry { RecipeInternalName = "Bread", Quantity = 2 } };
        var result = sut.Aggregate(entries, expansionDepth: 2, data, null, null, overrides);

        var bread = result.Single(r => r.ItemInternalName == "Bread");
        bread.TotalNeeded.Should().Be(2);
        bread.OnHandOverride.Should().Be(2);
        bread.IsCraftReady.Should().BeTrue();

        foreach (var name in new[] { "Butter", "Milk", "Salt" })
        {
            var row = result.Single(r => r.ItemInternalName == name);
            row.TotalNeeded.Should().Be(0);
            row.EffectiveOnHand.Should().Be(0);
            row.IsCraftReady.Should().BeTrue();
        }
    }

    [Fact]
    public void PartiallySatisfiedTarget_ReducesRawIngredients_ButKeepsChain()
    {
        var data = MakeStandardData();
        var sut = new RecipeAggregator();

        // 2 bread, override bread=1 → only 1 bread needs crafting. Ingredients scale to that 1 batch:
        // 1 butter + 1 salt from bread; butter expansion adds 2 milk + 1 salt. Total: 1 butter, 2 milk, 2 salt.
        var overrides = new Dictionary<string, int>(StringComparer.Ordinal) { ["Bread"] = 1 };
        var entries = new[] { new CraftListEntry { RecipeInternalName = "Bread", Quantity = 2 } };
        var result = sut.Aggregate(entries, expansionDepth: 2, data, null, null, overrides);

        result.Single(r => r.ItemInternalName == "Bread").TotalNeeded.Should().Be(2);
        result.Single(r => r.ItemInternalName == "Butter").TotalNeeded.Should().Be(1);
        result.Single(r => r.ItemInternalName == "Milk").TotalNeeded.Should().Be(2);
        result.Single(r => r.ItemInternalName == "Salt").TotalNeeded.Should().Be(2);
    }

    [Fact]
    public void OnHand_and_overrides_propagate_to_rows()
    {
        var data = MakeStandardData();
        var sut = new RecipeAggregator();

        var onHand = new Dictionary<string, int>(StringComparer.Ordinal) { ["Milk"] = 3 };
        var overrides = new Dictionary<string, int>(StringComparer.Ordinal) { ["Salt"] = 100 };

        var entries = new[] { new CraftListEntry { RecipeInternalName = "Butter", Quantity = 2 } };
        // depth 1 to pull the direct ingredients (Milk, Salt) into the result.
        var result = sut.Aggregate(entries, expansionDepth: 1, data, onHand, null, overrides);

        var milk = result.Single(r => r.ItemInternalName == "Milk");
        milk.OnHandDetected.Should().Be(3);
        milk.OnHandOverride.Should().BeNull();
        milk.Remaining.Should().Be(1); // needs 4, has 3

        var salt = result.Single(r => r.ItemInternalName == "Salt");
        salt.OnHandOverride.Should().Be(100);
        salt.Remaining.Should().Be(0);
        salt.IsCraftReady.Should().BeTrue();
    }
}
