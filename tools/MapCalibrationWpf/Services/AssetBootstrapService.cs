namespace Mithril.Tools.MapCalibrationWpf.Services;

using System.IO;
using Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Glues the common-lib asset extractors (<see cref="MapTextureExtractor"/>,
/// <see cref="IconTemplateExtractor"/>) onto a background <c>Task.Run</c> with
/// <see cref="IProgress{T}"/> updates so the UI dispatcher can show a modal
/// progress dialog without freezing.
///
/// <para>Both extractors are no-ops when their output is already cached
/// (<c>MapTextureExtractor.EnsureExtracted</c> checks bundle mtime;
/// <c>IconTemplateExtractor.EnsureExtracted</c> checks the cache version on
/// disk), so calling these on every area open is cheap once the first
/// extraction lands.</para>
/// </summary>
public sealed class AssetBootstrapService
{
    private readonly PgInstallResolver _installResolver;

    public AssetBootstrapService(PgInstallResolver installResolver)
    {
        _installResolver = installResolver;
    }

    public async Task<string> EnsureSourcePngAsync(string area, IProgress<string> progress)
    {
        var installPath = _installResolver.Resolve()
            ?? throw new UserFacingException(
                "PG install path not found; pick it from the dialog and retry.");
        var mapDir = RepoPaths.DefaultMapsCacheDir();
        progress.Report($"Locating Map_{area} bundle…");
        return await Task.Run(() =>
        {
            progress.Report($"Extracting Map_{area} from sharedassets bundle…");
            var pngPath = MapTextureExtractor.EnsureExtracted(installPath, mapDir, area);
            progress.Report($"Loaded {Path.GetFileName(pngPath)}");
            return pngPath;
        }).ConfigureAwait(false);
    }

    public async Task EnsureIconTemplatesAsync(IProgress<string> progress)
    {
        var installPath = _installResolver.Resolve()
            ?? throw new UserFacingException(
                "PG install path not found; pick it from the dialog and retry.");
        var iconsDir = RepoPaths.DefaultIconsCacheDir();
        var tpkPath = RepoPaths.DefaultTpkPath();
        progress.Report("Loading classdata.tpk…");
        await Task.Run(() =>
        {
            progress.Report("Decoding sharedassets0.assets via AssetsTools.NET…");
            IconTemplateExtractor.EnsureExtracted(installPath, iconsDir, tpkPath);
            progress.Report("Icon templates ready.");
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// True when no <c>index.json</c> exists in the icon cache directory — the
    /// signal that <see cref="IconTemplateExtractor.EnsureExtracted"/> has
    /// never successfully completed. Used to show/hide the header "Extract
    /// icon templates" action in <see cref="ViewModels.MainViewModel"/>.
    /// </summary>
    public static bool AreIconTemplatesMissing()
    {
        try
        {
            var iconsDir = RepoPaths.DefaultIconsCacheDir();
            return !File.Exists(Path.Combine(iconsDir, "index.json"));
        }
        catch (UserFacingException)
        {
            // Can't resolve the repo root → treat as missing so the user has a
            // visible affordance to retry.
            return true;
        }
    }
}
