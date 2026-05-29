using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

/// <summary>
/// Pulls the <c>Map_&lt;Area&gt;</c> Texture2D out of its per-area Addressables
/// bundle and writes a PNG. Mirrors the working path proven in
/// <c>tools/MapAssetSpike/Program.cs</c> — bundle filenames are lowercased,
/// each bundle contains exactly one Texture2D, no tpk needed because bundles
/// ship inline type trees.
/// </summary>
internal static class MapTextureExtractor
{
    public static string EnsureExtracted(string pgInstall, string mapDir, string area)
    {
        Directory.CreateDirectory(mapDir);
        var textureName = "Map_" + area;
        var outPng = Path.Combine(mapDir, textureName + ".png");

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
        bmp.Save(outPath, ImageFormat.Png);
    }
}
