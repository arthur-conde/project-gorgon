using System.Windows;
using System.Windows.Controls;
using Legolas.ViewModels;

namespace Legolas.Views;

public partial class LegolasPanelView : UserControl
{
    public LegolasPanelView()
    {
        InitializeComponent();
    }

    private void ShowInventory_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LegolasPanelViewModel vm)
            vm.Session.IsInventoryVisible = !vm.Session.IsInventoryVisible;
    }

    private void ShowMap_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LegolasPanelViewModel vm)
            vm.Session.IsMapVisible = !vm.Session.IsMapVisible;
    }
}
