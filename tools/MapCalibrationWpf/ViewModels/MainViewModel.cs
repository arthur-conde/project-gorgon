namespace Mithril.Tools.MapCalibrationWpf.ViewModels;

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Tools.MapCalibration.Common;
using Mithril.Tools.MapCalibrationWpf.Services;
using Mithril.Tools.MapCalibrationWpf.Views;

/// <summary>
/// Top-level workspace VM: holds the area picker, instantiates a per-area
/// <see cref="AreaWorkspaceViewModel"/> on selection. Guards the
/// <see cref="SelectedArea"/> change against uncommitted refs in the
/// outgoing workspace with a confirm-discard <c>MessageBox</c>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AreaCatalog _catalog;
    private readonly PgInstallResolver _installResolver;
    private bool _suppressGuard;

    public MainViewModel(AreaCatalog catalog, PgInstallResolver installResolver)
    {
        _catalog = catalog;
        _installResolver = installResolver;
        Areas = LoadAreas();
        IconTemplatesMissing = AssetBootstrapService.AreIconTemplatesMissing();
    }

    public IReadOnlyList<string> Areas { get; }

    [ObservableProperty]
    private string? _selectedArea;

    [ObservableProperty]
    private AreaWorkspaceViewModel? _workspace;

    /// <summary>
    /// True when the icon-templates cache is missing (no <c>index.json</c>
    /// in the icons cache dir). Drives the header-button visibility.
    /// Phase 1 surfaces this button so a user can prime the cache before
    /// future phases (NCC live overlay, screenshot-bbox modality) need it.
    /// </summary>
    [ObservableProperty]
    private bool _iconTemplatesMissing;

    [RelayCommand]
    private async Task ExtractIconTemplatesAsync()
    {
        ExtractionProgressDialog? dialog = null;
        try
        {
            dialog = new ExtractionProgressDialog("Preparing icon-template extraction…")
            {
                Owner = Application.Current?.MainWindow,
            };
            dialog.Show();

            var bootstrap = new AssetBootstrapService(_installResolver);
            var progress = new Progress<string>(msg => dialog?.UpdateStatus(msg));
            await bootstrap.EnsureIconTemplatesAsync(progress).ConfigureAwait(true);
            IconTemplatesMissing = AssetBootstrapService.AreIconTemplatesMissing();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Icon-template extraction failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            try { dialog?.Close(); } catch { /* dialog may have been closed already */ }
        }
    }

    partial void OnSelectedAreaChanged(string? value)
    {
        if (_suppressGuard) return;
        if (Workspace is { HasUncommittedRefs: true } outgoing)
        {
            var ok = MessageBox.Show(
                $"Discard uncommitted refs on {outgoing.Area} and switch to {value ?? "no area"}?",
                "Uncommitted refs",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (ok != MessageBoxResult.Yes)
            {
                // Revert SelectedArea without re-triggering this partial.
                _suppressGuard = true;
                try { SelectedArea = outgoing.Area; }
                finally { _suppressGuard = false; }
                return;
            }
        }
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
