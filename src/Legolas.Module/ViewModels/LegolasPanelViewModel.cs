using CommunityToolkit.Mvvm.ComponentModel;

namespace Legolas.ViewModels;

public sealed partial class LegolasPanelViewModel : ObservableObject
{
    public LegolasPanelViewModel(SessionState session,
                                 InventoryOverlayViewModel inventoryOverlay,
                                 MapOverlayViewModel mapOverlay,
                                 InventoryGridSettingsViewModel gridSettings,
                                 ControlPanelViewModel controlPanel,
                                 MotherlodeViewModel motherlode)
    {
        Session = session;
        InventoryOverlay = inventoryOverlay;
        MapOverlay = mapOverlay;
        GridSettings = gridSettings;
        ControlPanel = controlPanel;
        Motherlode = motherlode;
    }

    public SessionState Session { get; }
    public InventoryOverlayViewModel InventoryOverlay { get; }
    public MapOverlayViewModel MapOverlay { get; }
    public InventoryGridSettingsViewModel GridSettings { get; }
    public ControlPanelViewModel ControlPanel { get; }
    public MotherlodeViewModel Motherlode { get; }
}
