using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using FluentAssertions;
using Mithril.Shared.Wpf.Filtering;
using Mithril.Shared.Wpf.Sorting;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

public class SortFilterControllerTests
{
    private sealed record Item(string Name, int Value);

    private static (ListCollectionView View,
                    ObservableCollection<Item> Source,
                    ObservableCollection<ActiveSortKey<Item>> ActiveSortKeys,
                    IReadOnlyList<FilterPredicate<Item>> Filters)
        CreateRig(IEnumerable<Item> seed)
    {
        var coll = new ObservableCollection<Item>(seed);
        var view = new ListCollectionView(coll);
        var active = new ObservableCollection<ActiveSortKey<Item>>();
        var filters = new List<FilterPredicate<Item>>();
        return (view, coll, active, filters);
    }

    private static SortKey<Item> NameKey() => new("Name", "Name", "Name");
    private static SortKey<Item> ValueKey() => new("Value", "Value", "Value", DefaultDescending: true);

    [Fact]
    public void TwoActiveSortKeys_YieldSortDescriptionsInOrder()
    {
        var (view, _, active, filters) = CreateRig([new("a", 2), new("b", 1)]);
        active.Add(new(ValueKey(), ListSortDirection.Descending));
        active.Add(new(NameKey(),  ListSortDirection.Ascending));

        using var _c = new SortFilterController<Item>(view, active, filters);

        view.SortDescriptions.Should().HaveCount(2);
        view.SortDescriptions[0].PropertyName.Should().Be("Value");
        view.SortDescriptions[0].Direction.Should().Be(ListSortDirection.Descending);
        view.SortDescriptions[1].PropertyName.Should().Be("Name");
        view.SortDescriptions[1].Direction.Should().Be(ListSortDirection.Ascending);
    }

    [Fact]
    public void FlippingDirection_RebuildsSortDescriptions()
    {
        var (view, _, active, filters) = CreateRig([]);
        var entry = new ActiveSortKey<Item>(ValueKey(), ListSortDirection.Descending);
        active.Add(entry);
        using var _c = new SortFilterController<Item>(view, active, filters);

        entry.FlipDirection();

        view.SortDescriptions.Single().Direction.Should().Be(ListSortDirection.Ascending);
    }

    [Fact]
    public void RemovingActiveSortKey_RemovesSortDescription()
    {
        var (view, _, active, filters) = CreateRig([]);
        active.Add(new(ValueKey(), ListSortDirection.Descending));
        active.Add(new(NameKey(),  ListSortDirection.Ascending));
        using var _c = new SortFilterController<Item>(view, active, filters);

        active.RemoveAt(0);

        view.SortDescriptions.Should().ContainSingle()
            .Which.PropertyName.Should().Be("Name");
    }

    [Fact]
    public void MovingActiveSortKey_ReordersSortDescriptions()
    {
        var (view, _, active, filters) = CreateRig([]);
        active.Add(new(ValueKey(), ListSortDirection.Descending));
        active.Add(new(NameKey(),  ListSortDirection.Ascending));
        using var _c = new SortFilterController<Item>(view, active, filters);

        active.Move(0, 1);

        view.SortDescriptions[0].PropertyName.Should().Be("Name");
        view.SortDescriptions[1].PropertyName.Should().Be("Value");
    }

    [Fact]
    public void ActiveFilter_NarrowsTheView()
    {
        var items = new[] { new Item("a", 1), new Item("b", 2), new Item("c", 3) };
        var (view, _, active, _) = CreateRig(items);
        var filters = new List<FilterPredicate<Item>>
        {
            new("EvenValue", "Even", x => x.Value % 2 == 0, isActive: true),
        };
        using var _c = new SortFilterController<Item>(view, active, filters);

        view.Cast<Item>().Select(x => x.Name).Should().Equal("b");
    }

    [Fact]
    public void InactiveFilter_DoesNotNarrow()
    {
        var items = new[] { new Item("a", 1), new Item("b", 2) };
        var (view, _, active, _) = CreateRig(items);
        var filters = new List<FilterPredicate<Item>>
        {
            new("Always", "Always false", _ => false, isActive: false),
        };
        using var _c = new SortFilterController<Item>(view, active, filters);

        view.Cast<Item>().Should().HaveCount(2);
    }

    [Fact]
    public void TogglingIsActive_RefreshesView()
    {
        var items = new[] { new Item("a", 1), new Item("b", 2) };
        var (view, _, active, _) = CreateRig(items);
        var filter = new FilterPredicate<Item>("EvenValue", "Even", x => x.Value % 2 == 0, isActive: false);
        var filters = new List<FilterPredicate<Item>> { filter };
        using var _c = new SortFilterController<Item>(view, active, filters);

        view.Cast<Item>().Should().HaveCount(2);
        filter.IsActive = true;
        view.Cast<Item>().Should().HaveCount(1);
        filter.IsActive = false;
        view.Cast<Item>().Should().HaveCount(2);
    }

    [Fact]
    public void InvertedFilter_AppliesWhenInactive()
    {
        // "Show unknown"-style predicate: r => r.Value > 0 means "value-bearing rows".
        // Inverted=true → predicate applies when toggle is OFF (default), hiding zeros.
        // Toggling ON suppresses the predicate, revealing zero-value rows too.
        var items = new[] { new Item("a", 0), new Item("b", 1), new Item("c", 2) };
        var (view, _, active, _) = CreateRig(items);
        var filter = new FilterPredicate<Item>(
            "ShowZeroes", "Show zeroes",
            x => x.Value > 0,
            inverted: true,
            isActive: false);
        var filters = new List<FilterPredicate<Item>> { filter };
        using var _c = new SortFilterController<Item>(view, active, filters);

        // Off (default): predicate applies, zero hidden
        view.Cast<Item>().Select(i => i.Name).Should().Equal("b", "c");

        // On: predicate suppressed, all rows visible
        filter.IsActive = true;
        view.Cast<Item>().Select(i => i.Name).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void MultipleActiveFilters_AndCombine()
    {
        var items = new[]
        {
            new Item("a", 2), new Item("b", 4), new Item("aa", 4), new Item("bb", 6),
        };
        var (view, _, active, _) = CreateRig(items);
        var filters = new List<FilterPredicate<Item>>
        {
            new("Even", "Even", x => x.Value % 2 == 0, isActive: true),
            new("ShortName", "Single-char name", x => x.Name.Length == 1, isActive: true),
        };
        using var _c = new SortFilterController<Item>(view, active, filters);

        view.Cast<Item>().Select(i => i.Name).Should().Equal("a", "b");
    }

    [Fact]
    public void Dispose_StopsListening()
    {
        var (view, _, active, _) = CreateRig([]);
        var entry = new ActiveSortKey<Item>(ValueKey(), ListSortDirection.Descending);
        active.Add(entry);
        var ctrl = new SortFilterController<Item>(view, active, new List<FilterPredicate<Item>>());

        ctrl.Dispose();
        entry.FlipDirection();
        active.Add(new(NameKey(), ListSortDirection.Ascending));

        // Direction flip + new key arrived after Dispose; SortDescriptions snapshot
        // from before Dispose still has the original Descending Value.
        view.SortDescriptions.Should().ContainSingle()
            .Which.Direction.Should().Be(ListSortDirection.Descending);
    }
}
