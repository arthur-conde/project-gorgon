using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Mithril.Shared.Settings;

namespace Legolas.ViewModels;

/// <summary>
/// Composes the per-module settings groups (overlay options, click-through,
/// map colours, survey pin appearance, player pin appearance, active-pin
/// highlight, inventory grid) for the shell's per-module settings tab.
/// Bindings are intentionally pass-through to the existing sub-VMs / state
/// objects so the lift from <see cref="LegolasPanelView"/> is purely structural.
/// </summary>
public sealed partial class LegolasSettingsViewModel : ObservableObject
{
    public LegolasSettingsViewModel(
        SessionState session,
        ControlPanelViewModel controlPanel,
        InventoryGridSettingsViewModel gridSettings,
        LegolasSettings settings,
        LegolasBrushes brushes,
        SettingsAutoSaver<LegolasSettings> saver)
    {
        Session = session;
        ControlPanel = controlPanel;
        GridSettings = gridSettings;
        Colors = settings.Colors;
        PinStyle = settings.PinStyle;
        PlayerPinStyle = settings.PlayerPinStyle;
        ActivePinStyle = settings.ActivePinStyle;
        CalibrationPinStyle = settings.CalibrationPinStyle;
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
    public LegolasPinStyle PinStyle { get; }
    public LegolasPinStyle PlayerPinStyle { get; }
    public LegolasActivePinStyle ActivePinStyle { get; }
    public LegolasPinStyle CalibrationPinStyle { get; }
    public LegolasBrushes Brushes { get; }

    public IReadOnlyList<PinShape> PinShapes { get; } = Enum.GetValues<PinShape>();
    public IReadOnlyList<PinStrokeStyle> PinStrokeStyles { get; } = Enum.GetValues<PinStrokeStyle>();
    public IReadOnlyList<ActivePinTreatment> ActivePinTreatments { get; } = Enum.GetValues<ActivePinTreatment>();

    /// <summary>
    /// Outer diameter used by the in-settings preview rectangles. Larger than
    /// the typical map-overlay <c>SurveyPinRadiusMetres</c> so colour/stroke
    /// changes are visible without squinting; centre size and halo padding
    /// remain at their configured values so the user sees them in real terms.
    /// </summary>
    public double PreviewPinDiameter => 48.0;

    [ObservableProperty]
    private DateTimeOffset? _lastSavedAt;

    [ObservableProperty]
    private string _saveStatus = "No changes yet — settings are saved automatically.";

    [RelayCommand]
    private void ResetColorsToDefaults()
    {
        var defaults = new LegolasColors();
        Colors.RouteLine = defaults.RouteLine;
        Colors.BearingWedgeFill = defaults.BearingWedgeFill;
        Colors.BearingWedgeStroke = defaults.BearingWedgeStroke;
    }

    [RelayCommand]
    private void ResetPinStyleToDefaults()
    {
        var defaults = new LegolasPinStyle();
        CopyShape(defaults.Outer, PinStyle.Outer);
        CopyShape(defaults.Center, PinStyle.Center);
    }

    [RelayCommand]
    private void ResetPlayerPinStyleToDefaults()
    {
        var defaults = LegolasPinStyle.PlayerDefaults();
        CopyShape(defaults.Outer, PlayerPinStyle.Outer);
        CopyShape(defaults.Center, PlayerPinStyle.Center);
    }

    [RelayCommand]
    private void ResetCalibrationPinStyleToDefaults()
    {
        var defaults = LegolasPinStyle.CalibrationDefaults();
        CopyShape(defaults.Outer, CalibrationPinStyle.Outer);
        CopyShape(defaults.Center, CalibrationPinStyle.Center);
    }

    [RelayCommand]
    private void ResetActivePinStyleToDefaults()
    {
        var defaults = new LegolasActivePinStyle();
        ActivePinStyle.Treatment = defaults.Treatment;
        ActivePinStyle.Color = defaults.Color;
        ActivePinStyle.HaloThickness = defaults.HaloThickness;
        ActivePinStyle.HaloPaddingPx = defaults.HaloPaddingPx;
        ActivePinStyle.GlowBlurRadius = defaults.GlowBlurRadius;
    }

    private static void CopyShape(LegolasPinShapeStyle from, LegolasPinShapeStyle to)
    {
        to.Shape = from.Shape;
        to.FillColor = from.FillColor;
        to.StrokeColor = from.StrokeColor;
        to.StrokeStyle = from.StrokeStyle;
        to.StrokeThickness = from.StrokeThickness;
        to.Size = from.Size;
    }
}
