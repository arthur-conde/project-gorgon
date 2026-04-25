using System.Windows.Controls;
using System.Windows.Input;
using Bilbo.Domain;
using Bilbo.ViewModels;
using Mithril.Shared.Settings;
using Mithril.Shared.Wpf;

namespace Bilbo.Views;

public partial class StorageView : UserControl
{
    private readonly IItemDetailPresenter _itemDetail;

    public StorageView(BilboSettings settings, SettingsAutoSaver<BilboSettings> saver, IItemDetailPresenter itemDetail)
    {
        _itemDetail = itemDetail;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is StorageViewModel vm)
                DataGridStateBinder.Bind(ItemsGrid, settings.StorageGrid, vm.ApplyFilter, saver.Touch);
        };
    }

    private void ItemsGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only fire for double-clicks that land on an actual row — header / scrollbar / empty
        // space double-clicks bubble up here too.
        if (e.OriginalSource is not System.Windows.DependencyObject source) return;
        var row = FindAncestor<DataGridRow>(source);
        if (row is null) return;
        if (row.Item is not StorageItemRow item) return;
        if (string.IsNullOrEmpty(item.InternalName)) return;

        _itemDetail.Show(item.InternalName);
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
