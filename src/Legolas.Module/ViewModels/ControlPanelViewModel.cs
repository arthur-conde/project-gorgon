using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Flow;

namespace Legolas.ViewModels;

public sealed partial class ControlPanelViewModel : ObservableObject
{
    private readonly LegolasSettings _settings;

    public ControlPanelViewModel(LegolasSettings settings, SessionState session, SurveyFlowController surveyFlow)
    {
        _settings = settings;
        Session = session;
        SurveyFlow = surveyFlow;

        // Mirror external mutations of LegolasSettings back to our wrapper
        // properties. Every wrapper here is named 1:1 with its underlying
        // LegolasSettings property, so just forward the changed name. Without
        // this, a writer outside this VM (the #520 in-overlay nudge-pad
        // toggle writing directly to LegolasSettings.ShowNudgePadOnOverlay,
        // or any future cross-VM mutation of a wrapped setting) updates the
        // model but the LegolasSettingsView checkbox bound to
        // ControlPanel.<X> stays stale until the panel is reopened. Names
        // that don't have a wrapper here fire a no-op PropertyChanged that
        // no binding observes — cheaper than maintaining a per-name allow
        // list as wrappers come and go.
        _settings.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.PropertyName))
                OnPropertyChanged(e.PropertyName);
        };
    }

    public bool AutoResetWhenAllCollected
    {
        get => _settings.AutoResetWhenAllCollected;
        set
        {
            if (_settings.AutoResetWhenAllCollected == value) return;
            _settings.AutoResetWhenAllCollected = value;
            OnPropertyChanged();
        }
    }

    public LegolasSettings Settings => _settings;
    public SessionState Session { get; }
    public SurveyFlowController SurveyFlow { get; }

    public double SurveyDedupRadiusMetres
    {
        get => _settings.SurveyDedupRadiusMetres;
        set
        {
            var clamped = Math.Max(0, value);
            if (Math.Abs(_settings.SurveyDedupRadiusMetres - clamped) < 1e-6) return;
            _settings.SurveyDedupRadiusMetres = clamped;
            OnPropertyChanged();
        }
    }

    public double SurveyPinRadiusMetres
    {
        get => _settings.SurveyPinRadiusMetres;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 100);
            if (Math.Abs(_settings.SurveyPinRadiusMetres - clamped) < 1e-6) return;
            _settings.SurveyPinRadiusMetres = clamped;
            OnPropertyChanged();
        }
    }

    public bool ClickThroughMap
    {
        get => _settings.ClickThroughMap;
        set
        {
            if (_settings.ClickThroughMap == value) return;
            _settings.ClickThroughMap = value;
            OnPropertyChanged();
        }
    }

    public bool ClickThroughInventory
    {
        get => _settings.ClickThroughInventory;
        set
        {
            if (_settings.ClickThroughInventory == value) return;
            _settings.ClickThroughInventory = value;
            OnPropertyChanged();
        }
    }

    public bool AutoClickThroughInventoryDuringSession
    {
        get => _settings.AutoClickThroughInventoryDuringSession;
        set
        {
            if (_settings.AutoClickThroughInventoryDuringSession == value) return;
            _settings.AutoClickThroughInventoryDuringSession = value;
            OnPropertyChanged();
        }
    }

    public bool AutoHideOverlaysOnGameUnfocused
    {
        get => _settings.AutoHideOverlaysOnGameUnfocused;
        set
        {
            if (_settings.AutoHideOverlaysOnGameUnfocused == value) return;
            _settings.AutoHideOverlaysOnGameUnfocused = value;
            OnPropertyChanged();
        }
    }

    public bool HideOverlaysBetweenSessions
    {
        get => _settings.HideOverlaysBetweenSessions;
        set
        {
            if (_settings.HideOverlaysBetweenSessions == value) return;
            _settings.HideOverlaysBetweenSessions = value;
            OnPropertyChanged();
        }
    }

    public bool ShowNudgePadOnOverlay
    {
        get => _settings.ShowNudgePadOnOverlay;
        set
        {
            if (_settings.ShowNudgePadOnOverlay == value) return;
            _settings.ShowNudgePadOnOverlay = value;
            OnPropertyChanged();
        }
    }

    // #919: GameProcessName editor + DetectGameProcess command relocated to the
    // shared Game Configuration panel (GameConfigViewModel) so the map-calibration
    // capture engine can consume the value as shared infra. The Legolas tab now
    // shows only a read-only pointer to the new home.

    public double NudgeStepDefault
    {
        get => _settings.NudgeStepDefault;
        set
        {
            _settings.NudgeStepDefault = value;
            OnPropertyChanged();
        }
    }

    public double NudgeStepFast
    {
        get => _settings.NudgeStepFast;
        set
        {
            _settings.NudgeStepFast = value;
            OnPropertyChanged();
        }
    }

    public double NudgeStepFine
    {
        get => _settings.NudgeStepFine;
        set
        {
            _settings.NudgeStepFine = value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void StartSession() => SurveyFlow.Reset();

    [RelayCommand]
    private void MarkCurrentCollected()
    {
        var target = Session.Surveys.FirstOrDefault(s => s.IsActiveTarget)
                  ?? Session.Surveys.Where(s => !s.Collected).OrderBy(s => s.RouteOrder ?? int.MaxValue).FirstOrDefault();
        if (target is null) return;
        target.UpdateModel(target.Model with { Collected = true });
        Session.LastLogEvent = $"Manually marked: {target.Name}";
    }
}
