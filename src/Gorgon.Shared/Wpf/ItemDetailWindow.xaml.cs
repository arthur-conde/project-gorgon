using System.Windows;

namespace Gorgon.Shared.Wpf;

public partial class ItemDetailWindow : Window
{
    public ItemDetailWindow(ItemDetailViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
