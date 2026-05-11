using Mithril.Shared.Diagnostics;
using Mithril.Shared.Diagnostics.Performance;
using Mithril.Shared.Hotkeys;

namespace Mithril.Shell.Hotkeys;

/// <summary>
/// Toggles a perf-trace recording session. Gated on <see cref="ShellSettings.EnablePerfTrace"/>
/// — if the feature is off, presses are logged and ignored so an
/// accidental keystroke never silently starts a multi-MB trace file.
/// Marked developer-only so the binding doesn't clutter the player-facing
/// Hotkeys list. Ships unbound (per the shell's convention; see
/// <see cref="ForceQuitCommand"/>); suggested combo Ctrl+Alt+P.
/// </summary>
public sealed class StartPerfTraceHotkey : IHotkeyCommand
{
    private readonly ShellSettings _settings;
    private readonly PerfTracerHostedService _perf;
    private readonly IDiagnosticsSink _diagnostics;

    public StartPerfTraceHotkey(
        ShellSettings settings,
        PerfTracerHostedService perf,
        IDiagnosticsSink diagnostics)
    {
        _settings = settings;
        _perf = perf;
        _diagnostics = diagnostics;
    }

    public string Id => "mithril.shell.perf-trace.toggle";
    public string DisplayName => "Toggle perf-trace recording";
    public string? Category => "Shell · Diagnostics";
    public HotkeyBinding? DefaultBinding => null;
    public bool RespectsFocusGate => false;
    public bool IsDeveloperOnly => true;

    public Task ExecuteAsync(CancellationToken ct)
    {
        if (!_settings.EnablePerfTrace)
        {
            _diagnostics.Warn("PerfTrace",
                "Hotkey pressed but EnablePerfTrace=false — enable it in Settings first.");
            return Task.CompletedTask;
        }

        try
        {
            var wasActive = _perf.IsActive;
            _perf.Toggle();
            _diagnostics.Info("PerfTrace",
                wasActive ? "Recording stopped." : "Recording started.");
        }
        catch (Exception ex)
        {
            _diagnostics.Error("PerfTrace", $"Toggle failed: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}
