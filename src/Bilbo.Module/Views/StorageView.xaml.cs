using System.Windows.Controls;
using Bilbo.Domain;
using Bilbo.ViewModels;
using Gorgon.Shared.Settings;
using Gorgon.Shared.Wpf;

namespace Bilbo.Views;

public partial class StorageView : UserControl
{
    public StorageView(BilboSettings settings, SettingsAutoSaver<BilboSettings> saver)
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is StorageViewModel vm)
                DataGridStateBinder.Bind(ItemsGrid, settings.StorageGrid, vm.ApplyFilter, saver.Touch);
        };
    }
}
