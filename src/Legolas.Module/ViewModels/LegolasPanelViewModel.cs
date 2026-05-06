using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Flow;

namespace Legolas.ViewModels;

public sealed partial class LegolasPanelViewModel : ObservableObject
{
    public LegolasPanelViewModel(SessionState session,
                                 SurveyFlowController surveyFlow,
                                 MotherlodeFlowController motherlodeFlow,
                                 InventoryOverlayViewModel inventoryOverlay,
                                 MapOverlayViewModel mapOverlay,
                                 InventoryGridSettingsViewModel gridSettings,
                                 ControlPanelViewModel controlPanel,
                                 MotherlodeViewModel motherlode,
                                 LegolasColors colors,
                                 LegolasBrushes brushes)
    {
        Session = session;
        SurveyFlow = surveyFlow;
        MotherlodeFlow = motherlodeFlow;
        InventoryOverlay = inventoryOverlay;
        MapOverlay = mapOverlay;
        GridSettings = gridSettings;
        ControlPanel = controlPanel;
        Motherlode = motherlode;
        Colors = colors;
        Brushes = brushes;

        Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionState.IsMapVisible) or nameof(SessionState.IsInventoryVisible))
                OnPropertyChanged(nameof(OverlaysVisible));
        };
    }

    public SessionState Session { get; }
    public SurveyFlowController SurveyFlow { get; }
    public MotherlodeFlowController MotherlodeFlow { get; }
    public InventoryOverlayViewModel InventoryOverlay { get; }
    public MapOverlayViewModel MapOverlay { get; }
    public InventoryGridSettingsViewModel GridSettings { get; }
    public ControlPanelViewModel ControlPanel { get; }
    public MotherlodeViewModel Motherlode { get; }
    public LegolasColors Colors { get; }
    public LegolasBrushes Brushes { get; }

    /// <summary>True if either overlay is visible. Setter flips both together.</summary>
    public bool OverlaysVisible
    {
        get => Session.IsMapVisible || Session.IsInventoryVisible;
        set
        {
            Session.IsMapVisible = value;
            Session.IsInventoryVisible = value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void ToggleOverlays() => OverlaysVisible = !OverlaysVisible;

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
