using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Interop;

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

    public string GameProcessName
    {
        get => _settings.GameProcessName;
        set
        {
            if (string.Equals(_settings.GameProcessName, value, StringComparison.Ordinal)) return;
            _settings.GameProcessName = value;
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private string _detectGameProcessStatus = string.Empty;

    /// <summary>
    /// Capture the foreground window's process name 3 seconds after click,
    /// so the user has time to alt-tab to the game. Skips Mithril's own
    /// process — the foreground at click-time is always Mithril, so without
    /// the delay we'd just overwrite the setting with our own name.
    /// </summary>
    [RelayCommand]
    private async Task DetectGameProcessAsync()
    {
        for (var i = 3; i > 0; i--)
        {
            DetectGameProcessStatus = $"Switch to the game window... capturing in {i}s";
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        var hwnd = User32Focus.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            DetectGameProcessStatus = "No foreground window detected.";
            return;
        }

        User32Focus.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            DetectGameProcessStatus = "Could not resolve foreground process ID.";
            return;
        }

        if (pid == (uint)Environment.ProcessId)
        {
            DetectGameProcessStatus = "Mithril was foreground — switch to the game and try again.";
            return;
        }

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            GameProcessName = proc.ProcessName;
            DetectGameProcessStatus = $"Captured: {proc.ProcessName}";
        }
        catch (Exception ex)
        {
            DetectGameProcessStatus = $"Could not read process: {ex.Message}";
        }
    }

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
