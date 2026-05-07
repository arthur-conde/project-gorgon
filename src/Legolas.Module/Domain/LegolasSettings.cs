using System.ComponentModel;
using Mithril.Shared.Character;

namespace Legolas.Domain;

public sealed class LegolasSettings : INotifyPropertyChanged, IVersionedState<LegolasSettings>
{
    public const int Version = 2;
    public static int CurrentVersion => Version;

    /// <summary>
    /// v1 had no schema field; v2 introduces <see cref="LegolasPinStyle"/> +
    /// <see cref="LegolasActivePinStyle"/> + per-pin <see cref="PlayerPinStyle"/>
    /// and removes the old <c>PinPending</c>/<c>PinFinalized</c>/<c>PlayerMarker</c>
    /// fields from <see cref="LegolasColors"/>. Defaults to <c>1</c> (not
    /// <see cref="Version"/>) so that v1 JSON, which lacks the
    /// <c>schemaVersion</c> field, deserializes with the legacy version and
    /// triggers <see cref="Migrate"/>. Fresh in-memory instances also start at
    /// <c>1</c> — no-op migration since their legacy fields are null — and the
    /// loader bumps to current after persisting.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    public static LegolasSettings Migrate(LegolasSettings loaded)
    {
        if (loaded.SchemaVersion >= Version) return loaded;

        // v1 → v2: carry forward the user's customised pin colours into the
        // new style sub-states so they don't reset to defaults.
        //   * pinPending — single v1 colour, drove BOTH the survey pin's outer
        //     stroke AND its centre fill; lands in both fields.
        //   * pinFinalized — unused on main since #130; promoted to the
        //     active-pin highlight colour for users who customised it.
        //   * playerMarker — v1 player anchor's centre dot colour; lands in
        //     PlayerPinStyle.Center.FillColor (the outer ring was hardcoded
        //     white, so PlayerPinStyle.Outer.* keeps its v1-equivalent default).
        var legacyPending = loaded.Colors.LegacyPinPending;
        var legacyFinalized = loaded.Colors.LegacyPinFinalized;
        var legacyPlayer = loaded.Colors.LegacyPlayerMarker;

        if (!string.IsNullOrWhiteSpace(legacyPending))
        {
            loaded.PinStyle.Outer.StrokeColor = legacyPending;
            loaded.PinStyle.Center.FillColor = legacyPending;
        }
        if (!string.IsNullOrWhiteSpace(legacyFinalized))
        {
            loaded.ActivePinStyle.Color = legacyFinalized;
        }
        if (!string.IsNullOrWhiteSpace(legacyPlayer))
        {
            loaded.PlayerPinStyle.Center.FillColor = legacyPlayer;
        }

        loaded.Colors.LegacyPinPending = null;
        loaded.Colors.LegacyPinFinalized = null;
        loaded.Colors.LegacyPlayerMarker = null;
        return loaded;
    }

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
    public LegolasPinStyle PinStyle { get; set; } = new();
    public LegolasPinStyle PlayerPinStyle { get; set; } = LegolasPinStyle.PlayerDefaults();
    public LegolasActivePinStyle ActivePinStyle { get; set; } = new();
    public WindowLayout MapOverlay { get; set; } = new() { Width = 800, Height = 600 };
    public WindowLayout InventoryOverlay { get; set; } = new() { Width = 540, Height = 440 };

    // WPF stops hit-testing fully-transparent elements regardless of IsHitTestVisible,
    // so a 0-opacity overlay silently becomes unclickable. Floor at 1% — visually
    // indistinguishable from invisible, but the surface stays interactive.
    public const double MinInteractiveOpacity = 0.01;

    private double _mapOpacity = 1.0;
    public double MapOpacity
    {
        get => _mapOpacity;
        set
        {
            var clamped = Math.Max(value, MinInteractiveOpacity);
            if (Math.Abs(_mapOpacity - clamped) < 1e-6) return;
            _mapOpacity = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MapOpacity)));
        }
    }

    private double _inventoryOpacity = 1.0;
    public double InventoryOpacity
    {
        get => _inventoryOpacity;
        set
        {
            var clamped = Math.Max(value, MinInteractiveOpacity);
            if (Math.Abs(_inventoryOpacity - clamped) < 1e-6) return;
            _inventoryOpacity = clamped;
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
