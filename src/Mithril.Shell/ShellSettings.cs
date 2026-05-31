using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Mithril.Shared.Character;
using Mithril.Shared.Hotkeys;

namespace Mithril.Shell;

public sealed class ShellSettings : INotifyPropertyChanged, IActiveCharacterPersistence
{
    private string _gameRoot = "";
    public string GameRoot { get => _gameRoot; set => Set(ref _gameRoot, value); }

    /// <summary>
    /// Case-insensitive substring matched against the foreground window's
    /// process name to decide whether the game is in focus. Relocated here from
    /// <c>LegolasSettings</c> (#919) so the shared map-calibration capture engine
    /// can consume it without depending on a module. Mirrors the live
    /// <see cref="Mithril.Shared.Game.GameConfig.GameProcessName"/>.
    /// </summary>
    private string _gameProcessName = "ProjectGorgon";
    public string GameProcessName { get => _gameProcessName; set => Set(ref _gameProcessName, value); }

    /// <summary>
    /// RMS pixel-residual at or below which a calibration is considered "good".
    /// Relocated here from <c>LegolasSettings</c> (#919); mirrors the live
    /// <see cref="Mithril.Shared.Game.GameConfig.CalibrationGoodResidualPx"/>.
    /// </summary>
    private double _calibrationGoodResidualPx = 12.0;
    public double CalibrationGoodResidualPx { get => _calibrationGoodResidualPx; set => Set(ref _calibrationGoodResidualPx, value); }

    private string _activeModuleId = "";
    public string ActiveModuleId { get => _activeModuleId; set => Set(ref _activeModuleId, value); }

    private string? _activeCharacterName;
    public string? ActiveCharacterName { get => _activeCharacterName; set => Set(ref _activeCharacterName, value); }

    private string? _activeServer;
    public string? ActiveServer { get => _activeServer; set => Set(ref _activeServer, value); }

    private bool _developerMode;
    public bool DeveloperMode { get => _developerMode; set => Set(ref _developerMode, value); }

    /// <summary>Master toggle for the perf-trace harness. When false, the hotkey
    /// is a no-op and no WPF hooks are attached, so the feature costs nothing.</summary>
    private bool _enablePerfTrace;
    public bool EnablePerfTrace { get => _enablePerfTrace; set => Set(ref _enablePerfTrace, value); }

    /// <summary>When true, emit a <c>frame</c> event per render rather than
    /// aggregating to <c>frame_summary</c>. Use for short captures where
    /// per-frame fidelity matters; otherwise leave off to keep trace files small.</summary>
    private bool _verboseFrameEvents;
    public bool VerboseFrameEvents { get => _verboseFrameEvents; set => Set(ref _verboseFrameEvents, value); }

    /// <summary>When true (and <see cref="EnablePerfTrace"/> is true), starts a
    /// perf-trace session automatically right after the WPF Application is
    /// created in <c>Program.Main</c> — before the shell view-model resolves,
    /// so the initial <c>module_activated</c> event, first-frame render, and
    /// dispatcher-queue ramp during startup all land in the trace. Use this
    /// when investigating slow launches; otherwise leave off and toggle
    /// sessions via the hotkey for targeted captures.</summary>
    private bool _autoStartPerfTrace;
    public bool AutoStartPerfTrace { get => _autoStartPerfTrace; set => Set(ref _autoStartPerfTrace, value); }

    private string _uiFontFamily = "Segoe UI";
    public string UiFontFamily { get => _uiFontFamily; set => Set(ref _uiFontFamily, value); }

    private double _uiFontSize = 12.0;
    public double UiFontSize { get => _uiFontSize; set => Set(ref _uiFontSize, value); }

    private double _windowLeft = 200, _windowTop = 200, _windowWidth = 1100, _windowHeight = 700;
    public double WindowLeft { get => _windowLeft; set => Set(ref _windowLeft, value); }
    public double WindowTop { get => _windowTop; set => Set(ref _windowTop, value); }
    public double WindowWidth { get => _windowWidth; set => Set(ref _windowWidth, value); }
    public double WindowHeight { get => _windowHeight; set => Set(ref _windowHeight, value); }

    private double _sidebarWidth = 260.0;
    public double SidebarWidth { get => _sidebarWidth; set => Set(ref _sidebarWidth, value); }

    /// <summary>
    /// The persisted map-capture bbox (#947), in absolute virtual-desktop DIUs.
    /// <see langword="null"/> until the user first snips a region (the legitimate
    /// "no bbox set" state). Owned by the shell — not Legolas — because the shell
    /// references both Legolas and the Capture project, so a shell-side
    /// <c>ShellMapCaptureRectStore</c> can back the Capture-defined
    /// <c>IMapCaptureRectStore</c> seam without crossing the
    /// <c>Capture ↛ Legolas.Module</c> boundary. A nullable field is purely
    /// additive (missing key → null on load), so no schema migration is needed.
    /// </summary>
    private MapCaptureBbox? _mapCaptureBbox;
    public MapCaptureBbox? MapCaptureBbox { get => _mapCaptureBbox; set => Set(ref _mapCaptureBbox, value); }

    public Dictionary<string, HotkeyBinding> HotkeyBindings { get; set; } = new();
    public Dictionary<string, bool> ModuleEagerOverrides { get; set; } = new();

    private string? _lastDismissedUpdateVersion;
    public string? LastDismissedUpdateVersion { get => _lastDismissedUpdateVersion; set => Set(ref _lastDismissedUpdateVersion, value); }

    private double _updateCheckIntervalHours = 4.0;
    public double UpdateCheckIntervalHours { get => _updateCheckIntervalHours; set => Set(ref _updateCheckIntervalHours, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(f, v)) return;
        f = v;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

/// <summary>
/// The persisted map-capture bbox (#947), in absolute virtual-desktop DIUs (1 DIU =
/// 1/96"). A plain POCO so STJ source-gen serializes it as a nested object on
/// <see cref="ShellSettings.MapCaptureBbox"/>. The DIU frame matches WPF
/// <c>Window.Left/Top</c> and the snip's absolute virtual-desktop output; the
/// Capture project converts it to physical pixels at read time via the live
/// per-monitor DPI layout. Mirrors the Capture project's <c>MapCaptureRectDiu</c>
/// (kept separate so the shell-settings schema doesn't take a Capture dependency in
/// its persisted shape).
/// </summary>
public sealed class MapCaptureBbox
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ShellSettings))]
[JsonSerializable(typeof(MapCaptureBbox))]
public partial class ShellSettingsJsonContext : JsonSerializerContext { }
