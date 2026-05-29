namespace Mithril.Tools.MapCalibrationWpf.ViewModels;

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;
using Mithril.Tools.MapCalibrationWpf.Services;
using Mithril.Tools.MapCalibrationWpf.Views;

/// <summary>
/// Per-area state machine. On construction it kicks off
/// <see cref="AssetBootstrapService.EnsureSourcePngAsync"/> behind a modal
/// progress dialog; success populates <see cref="SourceMapImage"/>, failure
/// populates <see cref="LoadError"/>. The picker is instantiated synchronously
/// from the bundled landmarks/NPCs JSON (cheap; no IO except a JSON parse).
///
/// <para><see cref="Refs"/> is the user's committed reference clicks. The
/// solver wiring (Task 9) subscribes to <see cref="ObservableCollection{T}.CollectionChanged"/>
/// and re-solves on every change.</para>
/// </summary>
public sealed partial class AreaWorkspaceViewModel : ObservableObject
{
    private readonly PgInstallResolver _installResolver;

    public string Area { get; }

    [ObservableProperty]
    private BitmapImage? _sourceMapImage;

    [ObservableProperty]
    private string? _loadError;

    public LandmarkPickerViewModel Picker { get; }

    public ObservableCollection<RefViewModel> Refs { get; } = new();

    [ObservableProperty]
    private AreaCalibration? _calibration;

    /// <summary>
    /// True when the user has placed refs that haven't been committed to the
    /// baseline JSON yet. Wired in Task 11 — for now it just tracks the
    /// presence of refs so the area-switch guard has a signal to gate on.
    /// </summary>
    public bool HasUncommittedRefs => Refs.Count > 0;

    /// <summary>
    /// Click handler hook from <see cref="Views.SourceMapCanvas"/>. Materialises
    /// a <see cref="RefViewModel"/> from the picker's <see cref="LandmarkPickerViewModel.Selected"/>
    /// + the click pixel, then clears the picker selection. No-op when nothing
    /// is selected (so stray clicks don't pollute the ref table).
    /// </summary>
    public void PlaceRefAt(double x, double y)
    {
        if (Picker.Selected is not LandmarkPickerItem sel) return;
        var refVm = new RefViewModel(sel.Name, sel.Kind, sel.World, new PixelPoint(x, y));
        Refs.Add(refVm);
        Picker.Selected = null;
        // Task 9 hooks the solver here via Refs.CollectionChanged.
    }

    public AreaWorkspaceViewModel(string area, PgInstallResolver installResolver)
    {
        Area = area;
        _installResolver = installResolver;
        Picker = new LandmarkPickerViewModel(
            area,
            RepoPaths.LandmarksJsonPath(),
            RepoPaths.NpcsJsonPath());
        Refs.CollectionChanged += OnRefsChanged;
        // Fire-and-forget: the dialog runs modally on the UI thread; the bg work
        // updates UI properties when complete. Caller (MainViewModel) doesn't
        // await — area-switch responsiveness wins over linearised completion.
        _ = LoadAsync();
    }

    private void OnRefsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasUncommittedRefs));
        // Task 9 will call ReSolve() here.
    }

    private async Task LoadAsync()
    {
        ExtractionProgressDialog? dialog = null;
        try
        {
            var bootstrap = new AssetBootstrapService(_installResolver);

            dialog = new ExtractionProgressDialog($"Loading map for {Area}…")
            {
                Owner = Application.Current?.MainWindow,
            };
            dialog.Show();

            var progress = new Progress<string>(msg => dialog?.UpdateStatus(msg));
            var pngPath = await bootstrap.EnsureSourcePngAsync(Area, progress).ConfigureAwait(true);

            // CacheOption.OnLoad so the PNG file isn't held locked across
            // re-extracts; Freeze so the bound Image can render cross-thread.
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(pngPath);
            bmp.EndInit();
            bmp.Freeze();
            SourceMapImage = bmp;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }
        finally
        {
            try { dialog?.Close(); } catch { /* dialog may have been closed already */ }
        }
    }
}
