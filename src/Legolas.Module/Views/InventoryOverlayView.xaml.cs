using System.Windows;
using System.Windows.Input;
using Gorgon.Shared.Settings;
using Legolas.Controls;
using Legolas.Domain;

namespace Legolas.Views;

public partial class InventoryOverlayView : Window
{
    public InventoryOverlayView()
    {
        InitializeComponent();
    }

    public InventoryOverlayView(LegolasSettings settings, SettingsAutoSaver<LegolasSettings> saver) : this()
    {
        WindowLayoutBinder.Bind(this, settings.InventoryOverlay, saver.Touch);
        Loaded += (_, _) =>
        {
            ClickThrough.Apply(this, settings.ClickThroughInventory);
            ClickThrough.ForceTopmost(this);
        };
        Activated += (_, _) => ClickThrough.ForceTopmost(this);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LegolasSettings.ClickThroughInventory))
                ClickThrough.Apply(this, settings.ClickThroughInventory);
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }
}
