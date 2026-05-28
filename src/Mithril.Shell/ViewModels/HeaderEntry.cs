using CommunityToolkit.Mvvm.ComponentModel;

namespace Mithril.Shell.ViewModels;

/// <summary>
/// A single row in the telemetry headers DataGrid. Pure presentational —
/// the persistence is handled by <see cref="TelemetrySettingsViewModel.SaveHeaderCommand"/>
/// which wraps the value via <see cref="Mithril.Shared.Telemetry.Settings.HeaderValueProtection"/>
/// before storing into the settings dictionary.
/// </summary>
public sealed partial class HeaderEntry : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name;

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    private bool _isValueRevealed;

    /// <summary>
    /// True when the row has a non-blank name and is therefore eligible for
    /// persistence. Bound by the headers DataGrid's per-row Save button so
    /// blank rows can't be saved (Task 14 quality-review Minor #7 deferral).
    /// </summary>
    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    /// <summary>The header name as last persisted to
    /// <see cref="Mithril.Shared.Telemetry.Settings.TelemetrySettings.Headers"/>.
    /// Tracked so <see cref="TelemetrySettingsViewModel.SaveHeaderCommand"/> can
    /// remove the prior key when the user renames a header in-place — otherwise
    /// a typo fix would leak the typo'd entry indefinitely. Null until first save.</summary>
    public string? PersistedName { get; internal set; }

    public HeaderEntry(string name, string value, bool isValueRevealed)
    {
        _name = name;
        _value = value;
        _isValueRevealed = isValueRevealed;
    }
}
