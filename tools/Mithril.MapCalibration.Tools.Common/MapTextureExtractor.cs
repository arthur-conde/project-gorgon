using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Pulls the <c>Map_&lt;Area&gt;</c> Texture2D out of its per-area Addressables
/// bundle and writes a PNG. Mirrors the working path proven in
/// <c>tools/MapAssetSpike/Program.cs</c> — bundle filenames are lowercased,
/// each bundle contains exactly one Texture2D, no tpk needed because bundles
/// ship inline type trees.
///
/// <para>The extracted texture is rotated 180° to match the orientation PG
/// uses to render the in-game map (verified 2026-05-29 for Serbule — the
/// raw bundle texture is stored 180° from how it renders). The cached PNG
/// is what downstream consumers expect to see, so the rotation belongs in
/// the cache write rather than as a post-load step.</para>
/// </summary>
public static class MapTextureExtractor
{
    // Bump when the extracted PNG bytes change in a way that requires
    // re-extracting (e.g. the orientation fix). Stored as a suffix on the
    // filename; old files become orphans rather than getting silently
    // overwritten on a stale cache.
    private const int CacheFormatVersion = 4;

    public static string EnsureExtracted(string pgInstall, string mapDir, string area)
    {
        Directory.CreateDirectory(mapDir);
        var textureName = "Map_" + area;
        var outPng = Path.Combine(mapDir, $"{textureName}.v{CacheFormatVersion}.png");

        var bundleDir = SteamInstall.ResolveAreaBundleDir(pgInstall);
        var bundlePath = FindBundleForArea(bundleDir, area);

        if (File.Exists(outPng))
        {
            var srcMtime = File.GetLastWriteTimeUtc(bundlePath);
            var cacheMtime = File.GetLastWriteTimeUtc(outPng);
            if (cacheMtime >= srcMtime)
            {
                Console.WriteLine($"[map] cached: {outPng}");
                return outPng;
            }
            Console.WriteLine($"[map] cache stale (bundle patched); re-extracting");
        }

        Console.WriteLine($"[map] extracting {textureName} from {Path.GetFileName(bundlePath)}");

        var manager = new AssetsManager();
        var bunInst = manager.LoadBundleFile(bundlePath, true);
        var entries = bunInst.file.BlockAndDirInfo?.DirectoryInfos?.Count ?? 0;

        AssetsFileInstance? afileInst = null;
        for (int i = 0; i < entries; i++)
        {
            try { afileInst = manager.LoadAssetsFileFromBundle(bunInst, i); break; }
            catch { /* not an assets file (resS, manifest); keep trying */ }
        }
        if (afileInst is null)
        {
            throw new UserFacingException($"no loadable assets file in bundle {bundlePath}");
        }

        var tex2ds = afileInst.file.GetAssetsOfType(AssetClassID.Texture2D).ToList();
        AssetTypeValueField? target = null;
        foreach (var info in tex2ds)
        {
            var field = manager.GetBaseField(afileInst, info);
            if (field is null) continue;
            if (field["m_Name"].AsString == textureName)
            {
                target = field; break;
            }
        }
        // Fallback: per spike, each map bundle has exactly one Texture2D; if
        // naming has drifted, just take the only one.
        if (target is null && tex2ds.Count == 1)
        {
            target = manager.GetBaseField(afileInst, tex2ds[0]);
        }
        if (target is null)
        {
            throw new UserFacingException($"Texture2D '{textureName}' not found in bundle (had {tex2ds.Count} Texture2D entries)");
        }

        int width = target["m_Width"].AsInt;
        int height = target["m_Height"].AsInt;
        var texFile = TextureFile.ReadTextureFile(target);
        var encoded = texFile.FillPictureData(afileInst);
        var bgra = texFile.DecodeTextureRaw(encoded, useBgra: true);
        WritePng(bgra, width, height, outPng);
        Console.WriteLine($"[map] wrote {width}x{height} -> {outPng}");
        return outPng;
    }

    /// <summary>
    /// Returns the cached <c>Map_&lt;Area&gt;.v{N}.png</c> in <paramref name="mapDir"/>
    /// if it exists, without requiring a PG install / bundle decode. Used by the
    /// gate-study tool, which runs against pre-extracted textures. Returns null if
    /// no cached PNG is present.
    /// </summary>
    public static string? EnsureExtractedOrCached(string mapDir, string area)
    {
        var outPng = Path.Combine(mapDir, $"Map_{area}.v{CacheFormatVersion}.png");
        return File.Exists(outPng) ? outPng : null;
    }

    private static string FindBundleForArea(string bundleDir, string area)
    {
        // Bundle name convention (lowercase): maps_assets_assets_art_maps_map_<area>.png_<hash>.bundle
        var prefix = "maps_assets_assets_art_maps_map_" + area.ToLowerInvariant() + ".png_";
        var matches = Directory.EnumerateFiles(bundleDir, "*.bundle")
            .Where(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            throw new UserFacingException($"no map bundle for area '{area}' in {bundleDir} (looked for prefix '{prefix}')");
        }
        if (matches.Count > 1)
        {
            // Two patches' bundles can co-exist briefly during update; first one
            // matches the deterministic ordering the substrate spike validated.
            Console.WriteLine($"[map] {matches.Count} matches, picking first: {Path.GetFileName(matches[0])}");
        }
        return matches[0];
    }

    private static void WritePng(byte[] bgra32, int width, int height, string outPath)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = width * 4;
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(bgra32, row * rowBytes, data.Scan0 + row * data.Stride, rowBytes);
            }
        }
        finally { bmp.UnlockBits(data); }
        // PG's per-area bundle textures are stored mirrored across the X axis
        // (= top↔bottom vertical flip = Unity Y-up storage convention) from
        // the in-game map render. Apply the flip on save so the cached PNG
        // matches what users see in PG.
        //
        // System.Drawing naming gotcha: RotateNoneFlipY is the correct enum —
        // "FlipY" flips the Y coordinate (top↔bottom), i.e. mirrors ACROSS
        // the X axis. RotateNoneFlipX would be the opposite (left↔right).
        bmp.RotateFlip(RotateFlipType.RotateNoneFlipY);
        bmp.Save(outPath, ImageFormat.Png);
    }
}
