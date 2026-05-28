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
    private string _name;

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    private bool _isValueRevealed;

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
