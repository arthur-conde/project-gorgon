using System.Collections.ObjectModel;
using System.Windows.Data;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Pippin.Domain;
using Pippin.ViewModels;
using Xunit;

namespace Pippin.Tests;

/// <summary>
/// Repro for the "filter hides everything in the cards/grid view" report.
/// Verifies the QueryCompiler + ColumnBindingHelper combo against actual
/// FoodItemViewModel instances.
/// </summary>
public class GourmandFilterRepro
{
    private static FoodItemViewModel MakeFood(string name, string foodType, int level, int gourmandReq, bool eaten, int playerLevel = 33)
    {
        var entry = new FoodEntry(
            ItemId: 1,
            InternalName: name.Replace(" ", ""),
            Name: name,
            IconId: 100,
            FoodType: foodType,
            FoodLevel: level,
            GourmandLevelReq: gourmandReq,
            DietaryTags: new List<string> { "Vegetarian" });
        return new FoodItemViewModel(entry, eaten, eaten ? 1 : 0, playerLevel);
    }

    [Fact]
    public void Name_contains_cake_should_match_cakes()
    {
        var foods = new List<FoodItemViewModel>
        {
            MakeFood("3-Year Steamed Cake", "Meal", 30, 0, eaten: false),
            MakeFood("4-Year Steamed Cake", "Meal", 40, 0, eaten: false),
            MakeFood("Apple Juice", "Snack", 5, 0, eaten: true),
            MakeFood("Almonds", "Snack", 10, 0, eaten: true),
        };

        var columns = ColumnBindingHelper.BuildFromProperties(typeof(FoodItemViewModel));
        var predicate = QueryCompiler.Compile("Name CONTAINS \"cake\"", columns);

        predicate.Should().NotBeNull();
        var matches = foods.Where(f => predicate!(f)).ToList();
        matches.Should().HaveCount(2);
        matches.Select(f => f.Name).Should().BeEquivalentTo(new[] { "3-Year Steamed Cake", "4-Year Steamed Cake" });
    }

    [Fact]
    public void IsLocked_equals_true_should_match_locked_items()
    {
        var foods = new List<FoodItemViewModel>
        {
            MakeFood("Low Req", "Meal", 30, 10, eaten: false, playerLevel: 33), // not locked (req 10 <= 33)
            MakeFood("High Req", "Meal", 60, 60, eaten: false, playerLevel: 33), // LOCKED (req 60 > 33)
            MakeFood("Eaten High Req", "Meal", 60, 60, eaten: true, playerLevel: 33), // not locked (eaten)
        };

        var columns = ColumnBindingHelper.BuildFromProperties(typeof(FoodItemViewModel));
        var predicate = QueryCompiler.Compile("IsLocked = TRUE", columns);

        predicate.Should().NotBeNull();
        var matches = foods.Where(f => predicate!(f)).ToList();
        matches.Should().ContainSingle().Which.Name.Should().Be("High Req");
    }

    [Fact]
    public void Empty_query_returns_null_predicate()
    {
        var columns = ColumnBindingHelper.BuildFromProperties(typeof(FoodItemViewModel));
        var predicate = QueryCompiler.Compile("", columns);
        predicate.Should().BeNull();
    }

    [Fact]
    public void CollectionView_filter_pipeline_applies_predicate()
    {
        // Reproduce the GourmandViewModel + DataGrid filter composition end-to-end.
        var foods = new ObservableCollection<FoodItemViewModel>
        {
            MakeFood("3-Year Steamed Cake", "Meal", 30, 0, eaten: false),
            MakeFood("Apple Juice", "Snack", 5, 0, eaten: true),
            MakeFood("Almonds", "Snack", 10, 0, eaten: true),
        };

        var view = CollectionViewSource.GetDefaultView(foods);

        // Step 1: VM-level filter (PassesComboFilters with default settings — always true)
        Predicate<object> vmFilter = _ => true;
        view.Filter = vmFilter;

        // Step 2: DataGrid composes query predicate over vmFilter
        var columns = ColumnBindingHelper.BuildFromProperties(typeof(FoodItemViewModel));
        var queryPredicate = QueryCompiler.Compile("Name CONTAINS \"cake\"", columns);

        view.Filter = item =>
        {
            if (!vmFilter(item)) return false;
            if (queryPredicate is not null && !queryPredicate(item)) return false;
            return true;
        };

        var visible = view.Cast<FoodItemViewModel>().ToList();
        visible.Should().ContainSingle().Which.Name.Should().Be("3-Year Steamed Cake");

        // Step 3: clear query — composite should fall back to vmFilter only (all pass)
        view.Filter = item =>
        {
            if (!vmFilter(item)) return false;
            return true;
        };

        var visibleAfterClear = view.Cast<FoodItemViewModel>().ToList();
        visibleAfterClear.Should().HaveCount(3);
    }
}
