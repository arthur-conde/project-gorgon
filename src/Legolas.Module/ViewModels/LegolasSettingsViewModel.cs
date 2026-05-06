using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Mithril.Shared.Settings;

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
        LegolasBrushes brushes,
        SettingsAutoSaver<LegolasSettings> saver)
    {
        Session = session;
        ControlPanel = controlPanel;
        GridSettings = gridSettings;
        Colors = colors;
        Brushes = brushes;

        // Surface autosave activity in the footer so the user knows their
        // changes stuck. Without this, the slider tweak feels ambiguous —
        // did it persist? Saved event fires on the dispatcher thread.
        saver.Saved += time =>
        {
            LastSavedAt = time;
            SaveStatus = $"Saved at {time:HH:mm:ss}";
        };
    }

    public SessionState Session { get; }
    public ControlPanelViewModel ControlPanel { get; }
    public InventoryGridSettingsViewModel GridSettings { get; }
    public LegolasColors Colors { get; }
    public LegolasBrushes Brushes { get; }

    [ObservableProperty]
    private DateTimeOffset? _lastSavedAt;

    [ObservableProperty]
    private string _saveStatus = "No changes yet — settings are saved automatically.";

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
