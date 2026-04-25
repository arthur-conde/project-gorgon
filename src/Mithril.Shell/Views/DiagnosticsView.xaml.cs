using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mithril.Shell.ViewModels;

namespace Mithril.Shell.Views;

public partial class DiagnosticsView : System.Windows.Controls.UserControl
{
    private INotifyCollectionChanged? _observed;

    public DiagnosticsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_observed is not null) _observed.CollectionChanged -= OnEntriesChanged;
        _observed = null;
        if (DataContext is DiagnosticsViewModel vm && vm.Entries is INotifyCollectionChanged inc)
        {
            _observed = inc;
            inc.CollectionChanged += OnEntriesChanged;
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is not DiagnosticsViewModel vm) return;
        if (!vm.AutoScroll) return;
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        // Scroll to the end of the filtered view, not the source collection.
        var count = EntriesList.Items.Count;
        if (count == 0) return;
        EntriesList.ScrollIntoView(EntriesList.Items[count - 1]);
    }

    private void CategoryChip_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not CategoryToggle toggle) return;
        if (DataContext is not DiagnosticsViewModel vm) return;
        vm.CategoriesOnlyCommand.Execute(toggle);
        e.Handled = true;
    }
}
