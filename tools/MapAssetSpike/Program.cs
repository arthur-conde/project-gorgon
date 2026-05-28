// Research spike for mithril#827: prove PG map PNGs are extractable from the
// user's local Steam install of Project Gorgon. Standalone tool — NOT in
// Mithril.slnx, NOT a module, NOT productionized. Surface failures loudly so
// the findings comment can record what works and what doesn't.

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using Microsoft.Win32;

namespace Mithril.Tools.MapAssetSpike;

internal static class Program
{
    private const int PgAppId = 342940;
    private const string TargetTextureName = "Map_AreaSerbule";
    private const string BundleGlobLowercase = "maps_assets_assets_art_maps_map_areaserbule.png_*.bundle";

    private static int Main(string[] args)
    {
        Console.WriteLine("=== mithril#827 map-asset extraction spike ===");
        Console.WriteLine();

        try
        {
            var steamRoot = FindSteamRoot();
            Console.WriteLine($"[steam] root: {steamRoot}");

            var pgInstall = FindProjectGorgonInstall(steamRoot)
                ?? throw new InvalidOperationException(
                    $"AppID {PgAppId} not found in any Steam library. Is PG installed?");
            Console.WriteLine($"[steam] PG install: {pgInstall}");

            var streamingAssets = Path.Combine(
                pgInstall, "WindowsPlayer_Data", "StreamingAssets", "aa", "StandaloneWindows64");
            if (!Directory.Exists(streamingAssets))
            {
                throw new DirectoryNotFoundException(
                    $"StreamingAssets dir not found at {streamingAssets}");
            }
            Console.WriteLine($"[steam] StreamingAssets: {streamingAssets}");

            var bundlePath = FindBundle(streamingAssets, BundleGlobLowercase);
            Console.WriteLine($"[bundle] match: {Path.GetFileName(bundlePath)}");

            var outDir = Path.Combine(Path.GetTempPath(), "mithril-map-spike");
            Directory.CreateDirectory(outDir);
            var outPng = Path.Combine(outDir, $"{TargetTextureName}.png");
            if (File.Exists(outPng))
            {
                File.Delete(outPng);
            }

            ExtractTexture(bundlePath, TargetTextureName, outPng);

            VerifyPng(outPng);

            Console.WriteLine();
            Console.WriteLine("=== SPIKE PASS ===");
            Console.WriteLine($"PNG written to: {outPng}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("=== SPIKE FAIL ===");
            Console.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static string FindSteamRoot()
    {
        // Preferred: HKLM 64-bit redirected to Wow6432Node (Steam is 32-bit).
        // The InstallPath value is a string giving the directory containing steam.exe.
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var steamKey = hklm.OpenSubKey(@"SOFTWARE\Valve\Steam");
        if (steamKey?.GetValue("InstallPath") is string hklmPath && Directory.Exists(hklmPath))
        {
            return hklmPath;
        }

        // Fallback: current-user hive.
        using var steamCu = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
        if (steamCu?.GetValue("SteamPath") is string hkcuPath)
        {
            // HKCU SteamPath is forward-slashed on Windows for historical reasons.
            hkcuPath = hkcuPath.Replace('/', '\\');
            if (Directory.Exists(hkcuPath))
            {
                return hkcuPath;
            }
        }

        throw new InvalidOperationException(
            "Steam install path not found in HKLM\\SOFTWARE\\Valve\\Steam or HKCU\\SOFTWARE\\Valve\\Steam.");
    }

    private static string? FindProjectGorgonInstall(string steamRoot)
    {
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            return null;
        }

        // libraryfolders.vdf is Valve's nested-block text format. We don't need a
        // full parser: each library block has a `"path"` key and an `"apps"` block
        // whose keys are AppIDs. Scan blocks and pick the first one whose apps map
        // includes our AppID.
        var text = File.ReadAllText(vdf);
        var libraries = ParseLibraryFolders(text);
        foreach (var (libPath, appIds) in libraries)
        {
            if (appIds.Contains(PgAppId))
            {
                var candidate = Path.Combine(libPath, "steamapps", "common", "Project Gorgon");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    private static List<(string Path, HashSet<int> AppIds)> ParseLibraryFolders(string vdf)
    {
        // Minimal VDF tokenizer: we only need "path" and the int keys under "apps".
        // Library entries look like:
        //   "0"
        //   {
        //       "path"  "C:\\Program Files (x86)\\Steam"
        //       "apps"
        //       {
        //           "342940"  "12345678"
        //       }
        //   }
        var libs = new List<(string, HashSet<int>)>();
        var tokens = TokenizeVdf(vdf);

        // Walk top-level "libraryfolders" → numeric-keyed library blocks.
        int i = 0;
        // skip until we enter the first { (root "libraryfolders" block)
        while (i < tokens.Count && tokens[i] != "{") i++;
        if (i == tokens.Count) return libs;
        i++; // past root {

        while (i < tokens.Count && tokens[i] != "}")
        {
            // Expect: "<libIndex>" "{" ... "}"
            i++; // skip lib index key
            if (i >= tokens.Count || tokens[i] != "{") break;
            i++; // past {

            string? path = null;
            var appIds = new HashSet<int>();
            while (i < tokens.Count && tokens[i] != "}")
            {
                var key = tokens[i++];
                if (i >= tokens.Count) break;
                if (key == "path")
                {
                    path = tokens[i++];
                }
                else if (key == "apps" && tokens[i] == "{")
                {
                    i++; // past {
                    while (i < tokens.Count && tokens[i] != "}")
                    {
                        var appKey = tokens[i++];
                        if (i >= tokens.Count) break;
                        i++; // appKey's value (size bytes) — discard
                        if (int.TryParse(appKey, out var appId))
                        {
                            appIds.Add(appId);
                        }
                    }
                    if (i < tokens.Count) i++; // past closing }
                }
                else
                {
                    // Skip the value (could be a string or a nested block we don't care about).
                    if (tokens[i] == "{")
                    {
                        SkipBlock(tokens, ref i);
                    }
                    else
                    {
                        i++;
                    }
                }
            }
            if (i < tokens.Count) i++; // past lib block's closing }
            if (path != null)
            {
                libs.Add((path, appIds));
            }
        }
        return libs;
    }

    private static void SkipBlock(List<string> tokens, ref int i)
    {
        if (tokens[i] != "{") return;
        int depth = 1;
        i++;
        while (i < tokens.Count && depth > 0)
        {
            if (tokens[i] == "{") depth++;
            else if (tokens[i] == "}") depth--;
            i++;
        }
    }

    private static List<string> TokenizeVdf(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '{' || c == '}') { tokens.Add(c.ToString()); i++; continue; }
            if (c == '"')
            {
                // Quoted string with backslash escapes.
                i++;
                var sb = new System.Text.StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        char esc = text[i + 1];
                        sb.Append(esc switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            '\\' => '\\',
                            '"' => '"',
                            _ => esc,
                        });
                        i += 2;
                    }
                    else
                    {
                        sb.Append(text[i]);
                        i++;
                    }
                }
                tokens.Add(sb.ToString());
                if (i < text.Length) i++; // past closing "
                continue;
            }
            // Skip comments — VDF allows // line comments.
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            // Unquoted token — uncommon but possible.
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}')
            {
                i++;
            }
            tokens.Add(text[start..i]);
        }
        return tokens;
    }

    private static string FindBundle(string dir, string globLowercase)
    {
        // Filenames in StreamingAssets are lowercase by convention. The bundle's
        // <hash> suffix varies per patch — use the prefix match.
        var prefix = globLowercase[..globLowercase.IndexOf('*')];
        var matches = Directory.EnumerateFiles(dir, "*.bundle", SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            throw new FileNotFoundException($"No bundle matching '{globLowercase}' in {dir}");
        }
        if (matches.Count > 1)
        {
            Console.WriteLine($"[bundle] note: {matches.Count} matches, picking first.");
            foreach (var m in matches) Console.WriteLine($"           - {Path.GetFileName(m)}");
        }
        return matches[0];
    }

    private static void ExtractTexture(string bundlePath, string textureName, string outPng)
    {
        Console.WriteLine($"[extract] opening bundle: {bundlePath}");
        var manager = new AssetsManager();

        // AssetsTools.NET v3 loads the bundle and (if compressed lz4/lzma) unpacks
        // it into the BundleFileInstance. The boolean is `unpackIfPacked`.
        var bunInst = manager.LoadBundleFile(bundlePath, true);
        var bundleFile = bunInst.file;
        Console.WriteLine($"[extract] bundle version: header={bundleFile.Header.Version}, " +
                          $"engine={bundleFile.Header.EngineVersion}");

        // A bundle can contain multiple assets files. PG's map bundles ship one each.
        var assetsFileCount = bundleFile.BlockAndDirInfo?.DirectoryInfos?.Count ?? 0;
        Console.WriteLine($"[extract] entries in bundle: {assetsFileCount}");

        AssetsFileInstance? afileInst = null;
        for (int idx = 0; idx < assetsFileCount; idx++)
        {
            try
            {
                afileInst = manager.LoadAssetsFileFromBundle(bunInst, idx);
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[extract] entry {idx} not an assets file: {ex.Message}");
            }
        }
        if (afileInst is null)
        {
            throw new InvalidOperationException("No loadable assets file inside bundle.");
        }

        var afile = afileInst.file;
        Console.WriteLine($"[extract] assets file unity version: {afile.Metadata.UnityVersion}");

        var texInfos = afile.GetAssetsOfType(AssetClassID.Texture2D).ToList();
        Console.WriteLine($"[extract] Texture2D assets: {texInfos.Count}");

        AssetTypeValueField? targetField = null;
        AssetFileInfo? targetInfo = null;
        foreach (var info in texInfos)
        {
            var baseField = manager.GetBaseField(afileInst, info);
            var name = baseField["m_Name"].AsString;
            Console.WriteLine($"  - Texture2D '{name}' (pathId={info.PathId})");
            if (string.Equals(name, textureName, StringComparison.Ordinal))
            {
                targetField = baseField;
                targetInfo = info;
            }
        }

        if (targetField is null || targetInfo is null)
        {
            // Fall back: if there's exactly one Texture2D, use it.
            if (texInfos.Count == 1)
            {
                Console.WriteLine($"[extract] '{textureName}' not found by name; " +
                                  "falling back to the single Texture2D in this bundle.");
                targetInfo = texInfos[0];
                targetField = manager.GetBaseField(afileInst, targetInfo);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Texture '{textureName}' not found in bundle.");
            }
        }

        int width = targetField["m_Width"].AsInt;
        int height = targetField["m_Height"].AsInt;
        var format = (TextureFormat)targetField["m_TextureFormat"].AsInt;
        Console.WriteLine($"[extract] target: {width}x{height} format={format}");

        // TextureFile parses the Texture2D fields. The pixel bytes can live either
        // inline in the asset (m_StreamData empty) or in a sibling .resS file
        // (StreamingInfo set). FillPictureData(afileInst) loads either case —
        // returning the encoded bytes still in their on-disk texture format.
        var texFile = TextureFile.ReadTextureFile(targetField);
        var encoded = texFile.FillPictureData(afileInst);
        if (encoded is null || encoded.Length == 0)
        {
            throw new InvalidOperationException(
                $"FillPictureData returned empty bytes — missing streaming-data .resS? " +
                $"format={format} expected. Bundle may need SetPictureDataFromBundle instead.");
        }
        Console.WriteLine($"[extract] encoded payload: {encoded.Length:N0} bytes (format={format})");

        // DecodeTextureRaw → BGRA32 byte buffer. (DecodeTextureImage would write
        // PNG in one call, but it pulls in StbImageWriteSharp as a runtime
        // transitive that the NuGet package doesn't reference — fragile. We have
        // System.Drawing already, so re-encode here and own the dependency surface.)
        var bgra32 = texFile.DecodeTextureRaw(encoded, useBgra: true);
        if (bgra32 is null || bgra32.Length == 0)
        {
            throw new InvalidOperationException(
                $"DecodeTextureRaw returned empty bytes for format {format}.");
        }
        int expected = width * height * 4;
        if (bgra32.Length != expected)
        {
            Console.WriteLine($"[extract] note: decoded {bgra32.Length} bytes, " +
                              $"expected {expected} (continuing).");
        }

        WritePng(bgra32, width, height, outPng);
        Console.WriteLine($"[extract] PNG written: {outPng}");
    }

    private static void WritePng(byte[] bgra32, int width, int height, string outPath)
    {
        // bgra32 is top-to-bottom BGRA (Unity origin is bottom-left, but
        // DecodeTextureRaw already does the flip). Copy directly into a
        // Format32bppArgb bitmap — that pixel format is BGRA in memory on Windows.
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
        finally
        {
            bmp.UnlockBits(data);
        }
        bmp.Save(outPath, ImageFormat.Png);
    }

    private static void VerifyPng(string path)
    {
        Console.WriteLine();
        Console.WriteLine($"[verify] file: {path}");
        var info = new FileInfo(path);
        Console.WriteLine($"[verify] size: {info.Length:N0} bytes");

        // PNG magic: 89 50 4E 47 0D 0A 1A 0A
        byte[] magic = new byte[8];
        using (var fs = File.OpenRead(path))
        {
            int read = fs.Read(magic, 0, 8);
            if (read != 8)
            {
                throw new InvalidOperationException(
                    $"Could not read PNG header (got {read} bytes).");
            }
        }
        byte[] expected = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!magic.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"PNG magic mismatch: got {BitConverter.ToString(magic)}");
        }
        Console.WriteLine($"[verify] magic ok: {BitConverter.ToString(magic)}");

        using var img = Image.FromFile(path);
        Console.WriteLine($"[verify] dimensions: {img.Width}x{img.Height}");
        Console.WriteLine($"[verify] pixel format: {img.PixelFormat}");
    }
}
