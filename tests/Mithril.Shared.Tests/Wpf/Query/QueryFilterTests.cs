using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Data;
using FluentAssertions;
using Mithril.Reference.Models.Npcs;
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
    public void Order_clause_applies_sort_via_custom_sort()
    {
        // ORDER BY drives ListCollectionView.CustomSort (so string keys natural-sort);
        // SortDescriptions becomes empty as a side effect (WPF mutex). Verify the
        // observable behavior — the view is sorted in the requested order — rather
        // than the internal SortDescriptions state.
        RunOnSta(() =>
        {
            var rows = new ObservableCollection<Row>(Dataset);
            var listBox = new ListBox { ItemsSource = rows };
            QueryFilter.SetQueryText(listBox, "ORDER BY Samples DESC");
            QueryFilter.ForceAttachForTests(listBox);

            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(rows);
            view.CustomSort.Should().NotBeNull();
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
    public void Order_by_string_column_natural_sorts()
    {
        // Repro for issue #317: tiered ability names like "Bite", "Bite 2", …, "Bite 11"
        // would lex-sort to ["Bite", "Bite 10", "Bite 11", "Bite 2"]. Natural-sort
        // comparison restores the obvious order.
        RunOnSta(() =>
        {
            var rows = new ObservableCollection<Row>(new[]
            {
                new Row("Bite",     2,   true),
                new Row("Bite 11",  125, true),
                new Row("Bite 2",   10,  true),
                new Row("Bite 10",  116, true),
            });
            var listBox = new ListBox { ItemsSource = rows };
            QueryFilter.SetQueryText(listBox, "ORDER BY Crop");
            QueryFilter.ForceAttachForTests(listBox);

            var view = CollectionViewSource.GetDefaultView(rows);
            view.Cast<Row>().Select(r => r.Crop).Should().Equal(
                "Bite", "Bite 2", "Bite 10", "Bite 11");
        });
    }

    [Fact]
    public void Vm_set_sort_descriptions_restored_on_detach()
    {
        RunOnSta(() =>
        {
            var rows = new ObservableCollection<Row>(Dataset);
            var listBox = new ListBox { ItemsSource = rows };
            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(rows);
            view.SortDescriptions.Add(new SortDescription("Crop", ListSortDirection.Ascending));

            QueryFilter.ForceAttachForTests(listBox);
            QueryFilter.SetQueryText(listBox, "ORDER BY Samples DESC");
            QueryFilter.FlushPendingRebuildForTests(listBox);

            // Mid-attach: ORDER BY drives CustomSort; SortDescriptions is empty because
            // the two are mutex in ListCollectionView. Check the visible sort instead.
            view.CustomSort.Should().NotBeNull();
            view.Cast<Row>().Select(r => r.Samples).Should().BeInDescendingOrder();

            QueryFilter.ForceDetachForTests(listBox);

            view.CustomSort.Should().BeNull("Detach restores the VM's SortDescriptions baseline");
            view.SortDescriptions.Should().ContainSingle()
                .Which.PropertyName.Should().Be("Crop");
            view.Cast<Row>().Select(r => r.Crop).Should().BeInAscendingOrder();
        });
    }

    // Polymorphic-collection row exercising the QueryWarning attached DP wiring
    // (the channel PR-3 plumbs for the Silmarillion NPC tab). NpcService is an
    // Optional-narrowing hierarchy; CapIncreases is single-subtype (StoreService),
    // so an unguarded reference is a soft warning, never a hard error.
    private sealed record NpcRow(string Name, IReadOnlyList<NpcService> Services);

    private static ObservableCollection<NpcRow> NpcDataset() => new(new[]
    {
        new NpcRow("Vendor", new NpcService[]
        {
            new StoreService { Type = "Store", CapIncreases = ["Friends:5000:Armor"] },
            new AnimalHusbandryService { Type = "AnimalHusbandry" },
        }),
        new NpcRow("Trainer", new NpcService[]
        {
            new TrainingService { Type = "Training", Skills = ["Unarmed"] },
        }),
    });

    [Fact]
    public void Optional_unguarded_subtype_field_surfaces_QueryWarning_but_still_filters()
    {
        RunOnSta(() =>
        {
            var listBox = new ListBox { ItemsSource = NpcDataset() };
            QueryFilter.SetQueryText(listBox, "Services WITH ANY (CapIncreases CONTAINS 'Friends:5000:Armor')");
            QueryFilter.ForceAttachForTests(listBox);

            var view = CollectionViewSource.GetDefaultView(listBox.ItemsSource);
            view.Cast<NpcRow>().Select(r => r.Name).Should().BeEquivalentTo("Vendor");
            QueryFilter.GetQueryError(listBox).Should().BeNull();
            QueryFilter.GetQueryWarning(listBox).Should()
                .NotBeNullOrEmpty().And.Contain("CapIncreases");
        });
    }

    [Fact]
    public void Guarded_query_clears_QueryWarning()
    {
        RunOnSta(() =>
        {
            var listBox = new ListBox { ItemsSource = NpcDataset() };
            QueryFilter.SetQueryText(listBox,
                "Services WITH ANY (Type = 'Store' AND CapIncreases CONTAINS 'Friends:5000:Armor')");
            QueryFilter.ForceAttachForTests(listBox);

            var view = CollectionViewSource.GetDefaultView(listBox.ItemsSource);
            view.Cast<NpcRow>().Select(r => r.Name).Should().BeEquivalentTo("Vendor");
            QueryFilter.GetQueryWarning(listBox).Should().BeNull();
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
