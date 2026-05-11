using CommunityToolkit.Mvvm.ComponentModel;

namespace Mithril.Shell.ViewModels;

/// <summary>
/// View-model for the Settings → Diagnostics panel. Exposes the
/// perf-trace toggles directly off <see cref="ShellSettings"/> so the
/// checkboxes two-way-bind to the same persisted state the shell reads
/// at startup; no separate save step needed (autosave already handles it).
/// </summary>
public sealed partial class DiagnosticsSettingsViewModel : ObservableObject
{
    public ShellSettings Settings { get; }

    public DiagnosticsSettingsViewModel(ShellSettings settings)
    {
        Settings = settings;
    }

    /// <summary>The folder where perf-trace sessions land. Surfaced so the
    /// settings UI can offer a "Show me" button without duplicating the
    /// path-derivation logic from <c>Program.Main</c>.</summary>
    public string PerfDirectoryHint =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mithril", "Shell", "perf");
}
