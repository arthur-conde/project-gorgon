using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;

namespace Legolas.ViewModels;

/// <summary>
/// Composes the per-module settings groups (overlay options, click-through,
/// marker colors, inventory grid) for the shell's per-module settings tab.
/// Bindings are intentionally pass-through to the existing sub-VMs / state
/// objects so the lift from <see cref="LegolasPanelView"/> is purely structural.
/// </summary>
public sealed partial class LegolasSettingsViewModel : ObservableObject
{
    public LegolasSettingsViewModel(
        SessionState session,
        ControlPanelViewModel controlPanel,
        InventoryGridSettingsViewModel gridSettings,
        LegolasColors colors,
        LegolasBrushes brushes)
    {
        Session = session;
        ControlPanel = controlPanel;
        GridSettings = gridSettings;
        Colors = colors;
        Brushes = brushes;
    }

    public SessionState Session { get; }
    public ControlPanelViewModel ControlPanel { get; }
    public InventoryGridSettingsViewModel GridSettings { get; }
    public LegolasColors Colors { get; }
    public LegolasBrushes Brushes { get; }

    [RelayCommand]
    private void ResetColorsToDefaults()
    {
        var defaults = new LegolasColors();
        Colors.PinPending = defaults.PinPending;
        Colors.PinFinalized = defaults.PinFinalized;
        Colors.PlayerMarker = defaults.PlayerMarker;
        Colors.RouteLine = defaults.RouteLine;
        Colors.BearingWedgeFill = defaults.BearingWedgeFill;
        Colors.BearingWedgeStroke = defaults.BearingWedgeStroke;
    }
}
