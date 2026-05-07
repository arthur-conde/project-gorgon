using System.ComponentModel;

namespace Legolas.Domain;

public sealed class LegolasSettings : INotifyPropertyChanged
{
    private double _surveyDedupRadiusMetres = 5.0;
    public double SurveyDedupRadiusMetres
    {
        get => _surveyDedupRadiusMetres;
        set
        {
            var clamped = Math.Max(0, value);
            if (Math.Abs(_surveyDedupRadiusMetres - clamped) < 1e-6) return;
            _surveyDedupRadiusMetres = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SurveyDedupRadiusMetres)));
        }
    }

    private double _surveyPinRadiusMetres = 8.0;
    public double SurveyPinRadiusMetres
    {
        get => _surveyPinRadiusMetres;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 100.0);
            if (Math.Abs(_surveyPinRadiusMetres - clamped) < 1e-6) return;
            _surveyPinRadiusMetres = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SurveyPinRadiusMetres)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public InventoryGridSettings InventoryGrid { get; set; } = new();
    public LegolasColors Colors { get; set; } = new();
    public WindowLayout MapOverlay { get; set; } = new() { Width = 800, Height = 600 };
    public WindowLayout InventoryOverlay { get; set; } = new() { Width = 540, Height = 440 };

    private double _mapOpacity = 1.0;
    public double MapOpacity
    {
        get => _mapOpacity;
        set
        {
            if (Math.Abs(_mapOpacity - value) < 1e-6) return;
            _mapOpacity = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MapOpacity)));
        }
    }

    private double _inventoryOpacity = 1.0;
    public double InventoryOpacity
    {
        get => _inventoryOpacity;
        set
        {
            if (Math.Abs(_inventoryOpacity - value) < 1e-6) return;
            _inventoryOpacity = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InventoryOpacity)));
        }
    }

    public bool InvertDirections { get; set; }

    private bool _clickThroughInventory;
    public bool ClickThroughInventory
    {
        get => _clickThroughInventory;
        set
        {
            if (_clickThroughInventory == value) return;
            _clickThroughInventory = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClickThroughInventory)));
        }
    }

    private bool _clickThroughMap;
    public bool ClickThroughMap
    {
        get => _clickThroughMap;
        set
        {
            if (_clickThroughMap == value) return;
            _clickThroughMap = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClickThroughMap)));
        }
    }
    public bool ShowBearingWedges { get; set; } = true;
    public bool ShowRouteLabels { get; set; } = true;
    public int SurveyCount { get; set; } = 6;

    private bool _autoResetWhenAllCollected = true;
    public bool AutoResetWhenAllCollected
    {
        get => _autoResetWhenAllCollected;
        set
        {
            if (_autoResetWhenAllCollected == value) return;
            _autoResetWhenAllCollected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoResetWhenAllCollected)));
        }
    }

    private bool _autoClickThroughInventoryDuringSession = true;
    public bool AutoClickThroughInventoryDuringSession
    {
        get => _autoClickThroughInventoryDuringSession;
        set
        {
            if (_autoClickThroughInventoryDuringSession == value) return;
            _autoClickThroughInventoryDuringSession = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoClickThroughInventoryDuringSession)));
        }
    }

    private bool _autoHideOverlaysOnGameUnfocused = true;
    public bool AutoHideOverlaysOnGameUnfocused
    {
        get => _autoHideOverlaysOnGameUnfocused;
        set
        {
            if (_autoHideOverlaysOnGameUnfocused == value) return;
            _autoHideOverlaysOnGameUnfocused = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoHideOverlaysOnGameUnfocused)));
        }
    }

    private string _gameProcessName = "ProjectGorgon";
    public string GameProcessName
    {
        get => _gameProcessName;
        set
        {
            // Trim only — the predicate is a case-insensitive substring match,
            // so internal whitespace (e.g. "Project Gorgon") is allowed for
            // launchers that name the executable with a space.
            var v = value?.Trim() ?? string.Empty;
            if (string.Equals(_gameProcessName, v, StringComparison.Ordinal)) return;
            _gameProcessName = v;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GameProcessName)));
        }
    }

    private double _nudgeStepDefault = 1.0;
    public double NudgeStepDefault
    {
        get => _nudgeStepDefault;
        set
        {
            // Clamp to a positive minimum so a misconfigured setting can't make
            // the nudge keys silently no-op. Using DefaultValue here would leave
            // the user unable to override, so we just guard the lower bound.
            var clamped = value > 0 ? value : 1.0;
            if (Math.Abs(_nudgeStepDefault - clamped) < 1e-6) return;
            _nudgeStepDefault = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NudgeStepDefault)));
        }
    }

    private double _nudgeStepFast = 5.0;
    public double NudgeStepFast
    {
        get => _nudgeStepFast;
        set
        {
            var clamped = value > 0 ? value : 5.0;
            if (Math.Abs(_nudgeStepFast - clamped) < 1e-6) return;
            _nudgeStepFast = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NudgeStepFast)));
        }
    }

    private double _nudgeStepFine = 0.25;
    public double NudgeStepFine
    {
        get => _nudgeStepFine;
        set
        {
            var clamped = value > 0 ? value : 0.25;
            if (Math.Abs(_nudgeStepFine - clamped) < 1e-6) return;
            _nudgeStepFine = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NudgeStepFine)));
        }
    }
}

public sealed class WindowLayout
{
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 100;
    public double Width { get; set; } = 400;
    public double Height { get; set; } = 300;
}
