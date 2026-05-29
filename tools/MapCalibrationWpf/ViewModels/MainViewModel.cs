namespace Mithril.Tools.MapCalibrationWpf.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Tools.MapCalibration.Common;
using Mithril.Tools.MapCalibrationWpf.Services;

/// <summary>
/// Top-level workspace VM: holds the area picker, instantiates a per-area
/// <see cref="AreaWorkspaceViewModel"/> on selection. The dirty-state guard
/// for area switches lands in Task 11.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AreaCatalog _catalog;
    private readonly PgInstallResolver _installResolver;

    public MainViewModel(AreaCatalog catalog, PgInstallResolver installResolver)
    {
        _catalog = catalog;
        _installResolver = installResolver;
        Areas = LoadAreas();
    }

    public IReadOnlyList<string> Areas { get; }

    [ObservableProperty]
    private string? _selectedArea;

    [ObservableProperty]
    private AreaWorkspaceViewModel? _workspace;

    partial void OnSelectedAreaChanged(string? value)
    {
        Workspace = value is null
            ? null
            : new AreaWorkspaceViewModel(value, _installResolver);
    }

    private IReadOnlyList<string> LoadAreas()
    {
        try
        {
            return _catalog.ListAreasWithData(RepoPaths.LandmarksJsonPath());
        }
        catch (UserFacingException)
        {
            // Repo-relative resolution failed (running from outside the repo
            // tree). Surface an empty list — the picker will show no items
            // and the user can investigate via the console output.
            return [];
        }
    }
}
