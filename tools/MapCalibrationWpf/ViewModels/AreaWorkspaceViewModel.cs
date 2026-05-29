namespace Mithril.Tools.MapCalibrationWpf.ViewModels;

using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Tools.MapCalibration.Common;
using Mithril.Tools.MapCalibrationWpf.Services;
using Mithril.Tools.MapCalibrationWpf.Views;

/// <summary>
/// Per-area state machine. On construction it kicks off
/// <see cref="AssetBootstrapService.EnsureSourcePngAsync"/> behind a modal
/// progress dialog; success populates <see cref="SourceMapImage"/>, failure
/// populates <see cref="LoadError"/>.
///
/// <para>Tasks 7–10 layer the landmark picker, ref collection, solver, and
/// projection overlay on top.</para>
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

    /// <summary>
    /// Click handler hook from <see cref="Views.SourceMapCanvas"/>. Task 8
    /// fills this in to materialise a <c>RefViewModel</c> at the click pixel.
    /// </summary>
    public void PlaceRefAt(double x, double y)
    {
        // Task 8 implements this.
        _ = (x, y);
    }

    public AreaWorkspaceViewModel(string area, PgInstallResolver installResolver)
    {
        Area = area;
        _installResolver = installResolver;
        Picker = new LandmarkPickerViewModel(
            area,
            RepoPaths.LandmarksJsonPath(),
            RepoPaths.NpcsJsonPath());
        // Fire-and-forget: the dialog runs modally on the UI thread; the bg work
        // updates UI properties when complete. Caller (MainViewModel) doesn't
        // await — area-switch responsiveness wins over linearised completion.
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        ExtractionProgressDialog? dialog = null;
        try
        {
            var bootstrap = new AssetBootstrapService(_installResolver);

            // Surface the dialog only when extraction will actually take time —
            // EnsureExtracted is a fast file-stat when the cache is fresh.
            dialog = new ExtractionProgressDialog($"Loading map for {Area}…")
            {
                Owner = Application.Current?.MainWindow,
            };
            // Show non-modally; the await keeps the UI dispatcher pumping.
            // Show() returns immediately; the dialog stays on screen until
            // the explicit CompleteAndClose() / Close() below.
            dialog.Show();

            var progress = new Progress<string>(msg => dialog?.UpdateStatus(msg));
            var pngPath = await bootstrap.EnsureSourcePngAsync(Area, progress).ConfigureAwait(true);

            // Load with CacheOption.OnLoad so the PNG file doesn't stay
            // locked — the same file may be re-extracted on the next PG patch.
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
