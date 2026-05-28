using System.Windows.Input;

namespace Mithril.Shell.ViewModels;

/// <summary>
/// A chip representing a tag key the scrubber saw at runtime but the
/// <see cref="Mithril.Shared.Telemetry.Catalog.TagCatalog"/> doesn't know.
/// The user can promote it via <see cref="PromoteCommand"/>, which writes
/// an opt-in into <see cref="Mithril.Shared.Telemetry.Settings.TelemetrySettings.TagExports"/>
/// and removes the chip from the newly-seen list.
/// </summary>
public sealed class NewlySeenChip
{
    public string Key { get; }
    public ICommand PromoteCommand { get; }

    public NewlySeenChip(string key, ICommand promoteCommand)
    {
        Key = key;
        PromoteCommand = promoteCommand;
    }
}
