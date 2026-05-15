using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using FluentAssertions;
using Mithril.Shared.Wpf.Filtering;
using Mithril.Shared.Wpf.Query;
using Mithril.Shared.Wpf.Sorting;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

public class SortFilterControllerTests
{
    private sealed record Item(string Name, int Value);

    private static SortKey<Item> NameKey() => new("Name", "Name");
    private static SortKey<Item> ValueKey() => new("Value", "Value", DefaultDescending: true);

    private static (ListCollectionView View,
                    ObservableCollection<Item> Source,
                    List<OrderSpec> LastRewrite)
        CreateRig(IEnumerable<Item> seed)
    {
        var coll = new ObservableCollection<Item>(seed);
        var view = new ListCollectionView(coll);
        return (view, coll, new List<OrderSpec>());
    }

    private static SortFilterController<Item> Build(
        ListCollectionView view,
        IReadOnlyList<FilterPredicate<Item>> filters,
        Action<IReadOnlyList<OrderSpec>>? capture = null)
        => new(
            view,
            [NameKey(), ValueKey()],
            filters,
            capture ?? (_ => { }));

    [Fact]
    public void OnParsedOrderChanged_TwoSpecs_AppliesMultiKeySort()
    {
        // ORDER BY Value DESC, Name ASC: primary by Value descending, ties broken by
        // Name ascending. Group items by Value first, then sort within each tier.
        var items = new[] { new Item("b", 1), new Item("a", 2), new Item("a", 1) };
        var (view, _, _) = CreateRig(items);
        using var c = Build(view, []);

        c.OnParsedOrderChanged(
        [
            new OrderSpec("Value", OrderDirection.Descending),
            new OrderSpec("Name", OrderDirection.Ascending),
        ]);

        view.Cast<Item>().Select(x => (x.Name, x.Value)).Should().Equal(
            ("a", 2), ("a", 1), ("b", 1));
    }

    [Fact]
    public void OnParsedOrderChanged_Empty_ClearsAllSortState()
    {
        // ORDER BY drives ListCollectionView.CustomSort (not SortDescriptions — the two
        // are mutex in WPF). Empty order must clear CustomSort *and* leave SortDescriptions
        // empty so the view is restored to source order.
        var (view, _, _) = CreateRig([]);
        using var c = Build(view, []);
        c.OnParsedOrderChanged([new OrderSpec("Value", OrderDirection.Descending)]);
        view.CustomSort.Should().NotBeNull();

        c.OnParsedOrderChanged([]);

        view.CustomSort.Should().BeNull();
        view.SortDescriptions.Should().BeEmpty();
    }

    [Fact]
    public void OnParsedOrderChanged_UnknownColumn_LeavesNoSortState()
    {
        var (view, _, _) = CreateRig([]);
        using var c = Build(view, []);

        c.OnParsedOrderChanged([new OrderSpec("NoSuchColumn", OrderDirection.Ascending)]);

        view.SortDescriptions.Should().BeEmpty();
        view.CustomSort.Should().BeNull();
    }

    [Fact]
    public void OnParsedOrderChanged_StringKey_NaturalSorts()
    {
        // Lycanthropy-style tiered names: lex sort gives "Bite 10" before "Bite 2";
        // natural sort gives them in the order users expect.
        var items = new[]
        {
            new Item("Bite",    1),
            new Item("Bite 10", 1),
            new Item("Bite 11", 1),
            new Item("Bite 2",  1),
        };
        var (view, _, _) = CreateRig(items);
        using var c = Build(view, []);

        c.OnParsedOrderChanged([new OrderSpec("Name", OrderDirection.Ascending)]);

        view.Cast<Item>().Select(x => x.Name).Should().Equal(
            "Bite", "Bite 2", "Bite 10", "Bite 11");
    }

    [Fact]
    public void Chips_ProjectActiveAndInactiveKeys()
    {
        var (view, _, _) = CreateRig([]);
        using var c = Build(view, []);

        c.OnParsedOrderChanged([new OrderSpec("Value", OrderDirection.Descending)]);

        var chips = c.Chips;
        chips.Should().HaveCount(2);
        chips.Single(x => x.Key.Id == "Value").IsActive.Should().BeTrue();
        chips.Single(x => x.Key.Id == "Value").Direction.Should().Be(OrderDirection.Descending);
        chips.Single(x => x.Key.Id == "Name").IsActive.Should().BeFalse();
    }

    [Fact]
    public void ToggleChip_NotPresent_AppendsAtDefaultDirection()
    {
        var (view, _, captured) = CreateRig([]);
        IReadOnlyList<OrderSpec> last = [];
        using var c = Build(view, [], o => last = o);

        c.ToggleChip("Value");

        last.Should().ContainSingle();
        last[0].Column.Should().Be("Value");
        last[0].Direction.Should().Be(OrderDirection.Descending, "ValueKey has DefaultDescending: true");
    }

    [Fact]
    public void ToggleChip_AtDefaultDirection_Flips()
    {
        var (view, _, _) = CreateRig([]);
        IReadOnlyList<OrderSpec> last = [];
        using var c = Build(view, [], o => last = o);
        c.OnParsedOrderChanged([new OrderSpec("Value", OrderDirection.Descending)]);

        c.ToggleChip("Value");

        last.Should().ContainSingle();
        last[0].Direction.Should().Be(OrderDirection.Ascending);
    }

    [Fact]
    public void ToggleChip_AtFlippedDirection_Removes()
    {
        var (view, _, _) = CreateRig([]);
        IReadOnlyList<OrderSpec> last = [new OrderSpec("placeholder", OrderDirection.Ascending)];
        using var c = Build(view, [], o => last = o);
        // Value's default is Descending; Ascending is the "flipped" state → next toggle removes.
        c.OnParsedOrderChanged([new OrderSpec("Value", OrderDirection.Ascending)]);

        c.ToggleChip("Value");

        last.Should().BeEmpty();
    }

    [Fact]
    public void ActiveFilter_NarrowsTheView()
    {
        var items = new[] { new Item("a", 1), new Item("b", 2), new Item("c", 3) };
        var (view, _, _) = CreateRig(items);
        var filters = new List<FilterPredicate<Item>>
        {
            new("EvenValue", "Even", x => x.Value % 2 == 0, isActive: true),
        };
        using var c = Build(view, filters);

        view.Cast<Item>().Select(x => x.Name).Should().Equal("b");
    }

    [Fact]
    public void InactiveFilter_DoesNotNarrow()
    {
        var items = new[] { new Item("a", 1), new Item("b", 2) };
        var (view, _, _) = CreateRig(items);
        var filters = new List<FilterPredicate<Item>>
        {
            new("Always", "Always false", _ => false, isActive: false),
        };
        using var c = Build(view, filters);

        view.Cast<Item>().Should().HaveCount(2);
    }

    [Fact]
    public void TogglingIsActive_RefreshesView()
    {
        var items = new[] { new Item("a", 1), new Item("b", 2) };
        var (view, _, _) = CreateRig(items);
        var filter = new FilterPredicate<Item>("EvenValue", "Even", x => x.Value % 2 == 0, isActive: false);
        var filters = new List<FilterPredicate<Item>> { filter };
        using var c = Build(view, filters);

        view.Cast<Item>().Should().HaveCount(2);
        filter.IsActive = true;
        view.Cast<Item>().Should().HaveCount(1);
        filter.IsActive = false;
        view.Cast<Item>().Should().HaveCount(2);
    }

    [Fact]
    public void InvertedFilter_AppliesWhenInactive()
    {
        var items = new[] { new Item("a", 0), new Item("b", 1), new Item("c", 2) };
        var (view, _, _) = CreateRig(items);
        var filter = new FilterPredicate<Item>(
            "ShowZeroes", "Show zeroes",
            x => x.Value > 0,
            inverted: true,
            isActive: false);
        var filters = new List<FilterPredicate<Item>> { filter };
        using var c = Build(view, filters);

        view.Cast<Item>().Select(i => i.Name).Should().Equal("b", "c");

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
        var (view, _, _) = CreateRig(items);
        var filters = new List<FilterPredicate<Item>>
        {
            new("Even", "Even", x => x.Value % 2 == 0, isActive: true),
            new("ShortName", "Single-char name", x => x.Name.Length == 1, isActive: true),
        };
        using var c = Build(view, filters);

        view.Cast<Item>().Select(i => i.Name).Should().Equal("a", "b");
    }

    [Fact]
    public void Dispose_StopsListeningToFilters()
    {
        var items = new[] { new Item("a", 1), new Item("b", 2) };
        var (view, _, _) = CreateRig(items);
        var filter = new FilterPredicate<Item>("EvenValue", "Even", x => x.Value % 2 == 0, isActive: false);
        var ctrl = Build(view, [filter]);

        ctrl.Dispose();
        filter.IsActive = true;

        // Filter event no longer wired post-Dispose → the view was not refreshed.
        view.Cast<Item>().Should().HaveCount(2);
    }
}
