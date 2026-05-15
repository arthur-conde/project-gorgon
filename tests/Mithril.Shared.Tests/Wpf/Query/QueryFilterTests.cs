using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

/// <summary>
/// STA-thread tests for the <c>QueryFilter</c> attached behaviour. WPF
/// dependency objects require STA and a Dispatcher; we run each test inside
/// a short-lived STA thread and use the internal <c>ForceAttach</c>/<c>Flush</c>
/// hooks so we don't need a visual tree or a pumped dispatcher.
/// </summary>
public class QueryFilterTests
{
    private sealed record Row(string Crop, int Samples, bool Active);

    private static readonly Row[] Dataset =
    {
        new("Red Aster",  19, true),
        new("Daisy",       3, true),
        new("Tundra Rye",  3, false),
        new("Pumpkin",    12, true),
    };

    [Fact]
    public void Grammar_querytext_filters_underlying_collectionview()
    {
        RunOnSta(() =>
        {
            var listBox = new ListBox { ItemsSource = new ObservableCollection<Row>(Dataset) };
            QueryFilter.SetQueryText(listBox, "samples > 5 AND active = TRUE");
            QueryFilter.ForceAttachForTests(listBox);

            var view = CollectionViewSource.GetDefaultView(listBox.ItemsSource);
            var visible = view.Cast<Row>().ToArray();

            visible.Select(r => r.Crop).Should().BeEquivalentTo("Red Aster", "Pumpkin");
            QueryFilter.GetQueryError(listBox).Should().BeNull();
        });
    }

    [Fact]
    public void Bare_text_falls_back_to_substring_match_on_string_columns()
    {
        RunOnSta(() =>
        {
            var listBox = new ListBox { ItemsSource = new ObservableCollection<Row>(Dataset) };
            QueryFilter.SetQueryText(listBox, "aster");
            QueryFilter.ForceAttachForTests(listBox);

            var view = CollectionViewSource.GetDefaultView(listBox.ItemsSource);
            view.Cast<Row>().Select(r => r.Crop).Should().BeEquivalentTo("Red Aster");
        });
    }

    [Fact]
    public void Empty_querytext_does_not_remove_existing_vm_filter()
    {
        RunOnSta(() =>
        {
            var coll = new ObservableCollection<Row>(Dataset);
            var listBox = new ListBox { ItemsSource = coll };
            var view = CollectionViewSource.GetDefaultView(coll);

            // VM sets its own filter first.
            view.Filter = o => ((Row)o).Active;

            QueryFilter.SetQueryText(listBox, string.Empty);
            QueryFilter.ForceAttachForTests(listBox);

            view.Cast<Row>().Select(r => r.Crop).Should().BeEquivalentTo("Red Aster", "Daisy", "Pumpkin");
        });
    }

    [Fact]
    public void Querytext_composes_on_top_of_vm_filter()
    {
        RunOnSta(() =>
        {
            var coll = new ObservableCollection<Row>(Dataset);
            var listBox = new ListBox { ItemsSource = coll };
            var view = CollectionViewSource.GetDefaultView(coll);
            view.Filter = o => ((Row)o).Active; // VM excludes Tundra Rye

            QueryFilter.SetQueryText(listBox, "samples > 5");
            QueryFilter.ForceAttachForTests(listBox);

            view.Cast<Row>().Select(r => r.Crop).Should().BeEquivalentTo("Red Aster", "Pumpkin");
        });
    }

    [Fact]
    public void Detach_restores_original_vm_filter()
    {
        RunOnSta(() =>
        {
            var coll = new ObservableCollection<Row>(Dataset);
            var listBox = new ListBox { ItemsSource = coll };
            var view = CollectionViewSource.GetDefaultView(coll);
            Predicate<object> originalVmFilter = o => ((Row)o).Active;
            view.Filter = originalVmFilter;

            QueryFilter.SetQueryText(listBox, "samples > 5");
            QueryFilter.ForceAttachForTests(listBox);
            QueryFilter.ForceDetachForTests(listBox);

            view.Filter.Should().BeSameAs(originalVmFilter);
            view.Cast<Row>().Select(r => r.Crop).Should().BeEquivalentTo("Red Aster", "Daisy", "Pumpkin");
        });
    }

    [Fact]
    public void Malformed_querytext_populates_queryerror_without_clearing_view()
    {
        RunOnSta(() =>
        {
            var listBox = new ListBox { ItemsSource = new ObservableCollection<Row>(Dataset) };
            QueryFilter.SetQueryText(listBox, "samples > 5");
            QueryFilter.ForceAttachForTests(listBox);

            // Now make it malformed — should report an error and keep last-good predicate.
            QueryFilter.SetQueryText(listBox, "samples > >>");
            QueryFilter.FlushPendingRebuildForTests(listBox).Should().BeTrue();

            QueryFilter.GetQueryError(listBox).Should().NotBeNullOrEmpty();
            var view = CollectionViewSource.GetDefaultView(listBox.ItemsSource);
            view.Cast<Row>().Select(r => r.Crop).Should().BeEquivalentTo("Red Aster", "Pumpkin");
        });
    }

    [Fact]
    public void Order_clause_writes_sort_descriptions()
    {
        RunOnSta(() =>
        {
            var rows = new ObservableCollection<Row>(Dataset);
            var listBox = new ListBox { ItemsSource = rows };
            QueryFilter.SetQueryText(listBox, "ORDER BY Samples DESC");
            QueryFilter.ForceAttachForTests(listBox);

            var view = CollectionViewSource.GetDefaultView(rows);
            view.SortDescriptions.Should().ContainSingle()
                .Which.Should().BeEquivalentTo(new SortDescription("Samples", ListSortDirection.Descending));
            view.Cast<Row>().Select(r => r.Samples).Should().BeInDescendingOrder();
        });
    }

    [Fact]
    public void Clearing_order_clears_sort_descriptions()
    {
        RunOnSta(() =>
        {
            var rows = new ObservableCollection<Row>(Dataset);
            var listBox = new ListBox { ItemsSource = rows };
            QueryFilter.SetQueryText(listBox, "ORDER BY Samples");
            QueryFilter.ForceAttachForTests(listBox);

            QueryFilter.SetQueryText(listBox, "");
            QueryFilter.FlushPendingRebuildForTests(listBox);

            var view = CollectionViewSource.GetDefaultView(rows);
            view.SortDescriptions.Should().BeEmpty();
        });
    }

    [Fact]
    public void Vm_set_sort_descriptions_restored_on_detach()
    {
        RunOnSta(() =>
        {
            var rows = new ObservableCollection<Row>(Dataset);
            var listBox = new ListBox { ItemsSource = rows };
            var view = CollectionViewSource.GetDefaultView(rows);
            view.SortDescriptions.Add(new SortDescription("Crop", ListSortDirection.Ascending));

            QueryFilter.ForceAttachForTests(listBox);
            QueryFilter.SetQueryText(listBox, "ORDER BY Samples DESC");
            QueryFilter.FlushPendingRebuildForTests(listBox);

            view.SortDescriptions.Should().ContainSingle()
                .Which.PropertyName.Should().Be("Samples");

            QueryFilter.ForceDetachForTests(listBox);

            view.SortDescriptions.Should().ContainSingle()
                .Which.PropertyName.Should().Be("Crop");
            view.Cast<Row>().Select(r => r.Crop).Should().BeInAscendingOrder();
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (captured is not null) throw captured;
    }
}
