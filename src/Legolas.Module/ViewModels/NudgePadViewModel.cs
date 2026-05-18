using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;

namespace Legolas.ViewModels;

/// <summary>
/// On-screen nudge controls. Exposes four directional commands plus a
/// step-size tier so the user can fine-tune pin or anchor placement without
/// binding hotkeys. Shared between the wizard panel (always visible) and the
/// map overlay (gated by <c>LegolasSettings.ShowNudgePadOnOverlay</c>) so the
/// d-pad layout has one source of truth.
/// </summary>
public sealed partial class NudgePadViewModel : ObservableObject
{
    private readonly SessionState _session;
    private readonly MapOverlayViewModel _map;
    private readonly LegolasSettings _settings;

    public NudgePadViewModel(SessionState session, MapOverlayViewModel map, LegolasSettings settings)
    {
        _session = session;
        _map = map;
        _settings = settings;

        // The pad is "available" iff there's something to nudge — the same
        // precedence MapOverlayViewModel.Nudge applies: a selected #477A
        // calibration marker, the selected survey, or the #477C manual player
        // anchor. Recompute when any of those inputs change so the d-pad
        // enables/disables live (its root binds IsEnabled to IsAvailable).
        _session.PropertyChanged += OnSessionChanged;
        _session.Surveys.CollectionChanged += (_, _) => RaiseAvailability();
        _map.PropertyChanged += OnMapChanged;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionState.SelectedSurvey)
                           or nameof(SessionState.SurveyPlayerIsManual)
                           or nameof(SessionState.SurveyPlayerPixel)
                           or nameof(SessionState.Mode))
            RaiseAvailability();
    }

    private void OnMapChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapOverlayViewModel.HasSelectedCalibrationMarker))
            RaiseAvailability();
    }

    private void RaiseAvailability()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(NudgeTargetLabel));
    }

    private bool IsManualAnchorNudgeable =>
        _session.Mode == SessionMode.Survey
        && _session.SurveyPlayerIsManual
        && _session.SurveyPlayerPixel is not null;

    /// <summary>True iff there is a nudge target: a selected calibration
    /// marker (#477A), a selected survey pin, or the manual player anchor
    /// (#477C).</summary>
    public bool IsAvailable =>
        _map.HasSelectedCalibrationMarker
        || _session.SelectedSurvey is not null
        || IsManualAnchorNudgeable;

    /// <summary>
    /// Short human label of what the buttons will move. Mirrors the
    /// <see cref="MapOverlayViewModel.Nudge"/> precedence.
    /// </summary>
    public string NudgeTargetLabel
    {
        get
        {
            if (_map.HasSelectedCalibrationMarker) return "Calibration pin";
            if (_session.SelectedSurvey is { } sel) return $"Pin: {sel.Name}";
            if (IsManualAnchorNudgeable) return "Your position";
            return "(no target — select a pin)";
        }
    }

    [ObservableProperty]
    private NudgeStepSize _selectedStep = NudgeStepSize.Default;

    private double Magnitude => SelectedStep switch
    {
        NudgeStepSize.Fine => _settings.NudgeStepFine,
        NudgeStepSize.Fast => _settings.NudgeStepFast,
        _ => _settings.NudgeStepDefault,
    };

    [RelayCommand]
    private void NudgeUp() => _map.Nudge(0, -1, Magnitude);

    [RelayCommand]
    private void NudgeDown() => _map.Nudge(0, 1, Magnitude);

    [RelayCommand]
    private void NudgeLeft() => _map.Nudge(-1, 0, Magnitude);

    [RelayCommand]
    private void NudgeRight() => _map.Nudge(1, 0, Magnitude);
}
