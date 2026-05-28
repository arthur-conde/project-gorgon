using CommunityToolkit.Mvvm.ComponentModel;

namespace Mithril.Shell.ViewModels;

/// <summary>
/// View-model for the Settings → Diagnostics panel. Exposes the
/// perf-trace toggles directly off <see cref="ShellSettings"/> so the
/// checkboxes two-way-bind to the same persisted state the shell reads
/// at startup; no separate save step needed (autosave already handles it).
///
/// Hosts the Telemetry sub-section VM (<see cref="TelemetrySettingsViewModel"/>)
/// as a composed child so the Diagnostics page renders both perf-trace
/// controls and the OTLP export configuration in one place — see mithril#815.
/// </summary>
public sealed partial class DiagnosticsSettingsViewModel : ObservableObject
{
    public ShellSettings Settings { get; }

    /// <summary>
    /// The OTLP-export sub-section VM. Always present — ShellComposition
    /// registers fallback singletons for the scrubber-graph collaborators when
    /// AddMithrilOtlpExport is no-op (EnableOtlpExport=false) so the master
    /// toggle and connection fields remain editable. The user can opt in,
    /// restart, and the live exporter picks up the persisted settings.
    /// </summary>
    public TelemetrySettingsViewModel Telemetry { get; }

    public DiagnosticsSettingsViewModel(ShellSettings settings, TelemetrySettingsViewModel telemetry)
    {
        Settings = settings;
        Telemetry = telemetry;
    }

    /// <summary>The folder where perf-trace sessions land. Surfaced so the
    /// settings UI can offer a "Show me" button without duplicating the
    /// path-derivation logic from <c>Program.Main</c>.</summary>
    public string PerfDirectoryHint =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mithril", "Shell", "perf");
}
