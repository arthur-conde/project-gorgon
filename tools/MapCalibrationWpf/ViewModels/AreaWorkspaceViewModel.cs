namespace Mithril.Tools.MapCalibrationWpf.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Tools.MapCalibrationWpf.Services;

/// <summary>
/// Per-area state machine. Task 4 fills in just the <see cref="Area"/> handle
/// so the area picker can spin one up; the asset bootstrap + landmark picker +
/// solver wiring land in Tasks 5–10.
/// </summary>
public sealed partial class AreaWorkspaceViewModel : ObservableObject
{
    public string Area { get; }

    public AreaWorkspaceViewModel(string area, PgInstallResolver installResolver)
    {
        Area = area;
        _ = installResolver; // wired in Task 5
    }
}
