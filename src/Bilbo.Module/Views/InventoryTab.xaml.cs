using System.Windows.Controls;
using System.Windows.Input;
using Bilbo.Domain;
using Bilbo.ViewModels;
using Mithril.Shared.Wpf;

namespace Bilbo.Views;

public partial class InventoryTab : UserControl
{
    public InventoryTab()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is InventoryTabViewModel vm)
        {
            DataGridStateBinder.Bind(ItemsGrid, vm.Settings.StorageGrid, vm.Storage.ApplyFilter, vm.Saver.Touch);
        }
    }

    private void ItemsGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only fire for double-clicks that land on an actual row — header / scrollbar / empty
        // space double-clicks bubble up here too.
        if (e.OriginalSource is not System.Windows.DependencyObject source) return;
        var row = FindAncestor<DataGridRow>(source);
        if (row?.Item is not StorageItemRow item || string.IsNullOrEmpty(item.InternalName)) return;
        if (DataContext is not InventoryTabViewModel vm) return;

        vm.ItemDetail.Show(item.InternalName);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(System.Windows.DependencyObject start) where T : System.Windows.DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
