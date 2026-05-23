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

    /// <summary>When true, the L0.5 classifier (#532 / #556 /
    /// <c>PlayerLogClassifier</c>) fills
    /// <see cref="Mithril.Shared.Logging.LocalPlayerLogLine.Raw"/> (and the
    /// equivalent fields on the combat / system pipes) with the exact source
    /// <see cref="Mithril.Shared.Logging.RawLogLine.Line"/>. <c>null</c> by
    /// default — no per-line string allocation. Use when diagnosing an
    /// L2 / L3 parse or interpretation failure where the original line is
    /// useful at the failing datum. Flip takes effect forward (no restart).
    /// Infra-level diagnostic; sibling to <see cref="VerboseFrameEvents"/>
    /// and <see cref="MirrorRawLogLinesToDiagnostics"/>.</summary>
    private bool _captureRawPlayerLogLines;
    public bool CaptureRawPlayerLogLines { get => _captureRawPlayerLogLines; set => Set(ref _captureRawPlayerLogLines, value); }

    /// <summary>When true, every Player.log and ChatLog tail line is mirrored
    /// into the Mithril diagnostics sink (the in-memory ring buffer powering
    /// the live Diagnostics view + the rolling <c>mithril-*.json</c> Serilog
    /// file at Verbose). Default OFF — when off, the per-line
    /// <see cref="Mithril.Shared.Diagnostics.IDiagnosticsSink.Trace"/> fanout
    /// + Serilog Verbose write disappear from the L0 poll thread, closing
    /// #507's hot-path cost (DiagnosticsSink <c>EntryAdded</c> fanout + per-line
    /// unbuffered disk write under bursts). Turn on only when actively
    /// diagnosing Mithril behavior or running ad-hoc <c>mithril-logs</c> MCP
    /// analytics; flip takes effect forward (no restart). Infra-level
    /// diagnostic; sibling to <see cref="CaptureRawPlayerLogLines"/>.</summary>
    private bool _mirrorRawLogLinesToDiagnostics;
    public bool MirrorRawLogLinesToDiagnostics { get => _mirrorRawLogLinesToDiagnostics; set => Set(ref _mirrorRawLogLinesToDiagnostics, value); }

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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ShellSettings))]
public partial class ShellSettingsJsonContext : JsonSerializerContext { }
