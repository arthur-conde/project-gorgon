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

    [RelayCommand]
    private void StartSession() => SurveyFlow.Reset();

    [RelayCommand]
    private void SetPlayerPosition() => SurveyFlow.RequestSetPlayerPosition();

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
