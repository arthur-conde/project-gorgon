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

        // The pad is "available" iff there's something to nudge: a selected
        // survey, OR the player anchor while it's still editable. Recompute
        // when either input changes so buttons enable/disable live.
        _session.PropertyChanged += OnSessionChanged;
        _session.Surveys.CollectionChanged += (_, _) => RaiseAvailability();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionState.SelectedSurvey))
            RaiseAvailability();
    }

    private void RaiseAvailability()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(NudgeTargetLabel));
    }

    /// <summary>True iff a survey pin is selected to nudge (#454: the
    /// editable anchor is retired).</summary>
    public bool IsAvailable => _session.SelectedSurvey is not null;

    /// <summary>
    /// Short human label of what the buttons will move. Used by the panel-side
    /// pad so the user can tell at a glance which pin they're nudging.
    /// </summary>
    public string NudgeTargetLabel
    {
        get
        {
            var sel = _session.SelectedSurvey;
            return sel is not null ? $"Pin: {sel.Name}" : "(no target — select a pin)";
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
