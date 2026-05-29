// Research probe for mithril#848: enumerate PG Addressables + scene-level
// assets to find any source of per-area map-icon PIXEL positions (landmarks /
// NPCs / POIs). Standalone tool — NOT in Mithril.slnx, NOT productionized.
//
// Phases (selected via first CLI arg, default "inventory"):
//   inventory  — list every bundle + asset-type histogram (Texture2D, Sprite,
//                MonoBehaviour, GameObject, ScriptableObject) → see at a glance
//                which bundles carry structured data vs. raw textures.
//   names      — across all bundles + globalgamemanagers.assets, dump every
//                asset whose m_Name contains any of the icon/landmark/NPC
//                keywords. The candidate list to manually inspect.
//   dumpbundle <substring>
//              — pick the first bundle whose filename contains <substring>,
//                walk every asset, dump (type, name, pathId) + for
//                MonoBehaviours / ScriptableObjects dump the JSON-ish field
//                tree (truncated). Use after `names` finds something
//                interesting.
//   ggm        — open globalgamemanagers.assets at PG root and dump every
//                non-builtin asset (ScriptableObjects are often holders for
//                project-wide tables / atlas references).
//
// Findings get pasted into the spike write-up; this tool's job is to make
// "no, the data isn't here" provable rather than asserted.

using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Microsoft.Win32;

namespace Mithril.Tools.MapIconProbe;

internal static class Program
{
    private const int PgAppId = 342940;

    // Substrings we're hoping to see on a MonoBehaviour, ScriptableObject, or
    // Sprite that would tie an in-world entity to a map-pixel position.
    // Intentionally broad; the `names` phase prints everything that hits so a
    // human eye can dismiss false positives.
    private static readonly string[] IconKeywords =
    [
        "landmark", "minimap", "mapicon", "mapmarker", "mapmark",
        "waypoint", "mapdata", "mapmeta", "mapref", "mappoi",
        "spritesheet", "spriteatlas", "areamap", "worldmap",
        "mapnpc", "npcmap", "mapdb",
    ];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== mithril#848 map-icon-position probe ===");
        Console.WriteLine();

        var phase = args.Length > 0 ? args[0] : "inventory";

        try
        {
            var pgInstall = ResolvePgInstall();
            Console.WriteLine($"[steam] PG install: {pgInstall}");

            var bundlesDir = Path.Combine(pgInstall,
                "WindowsPlayer_Data", "StreamingAssets", "aa", "StandaloneWindows64");
            var ggmPath = Path.Combine(pgInstall,
                "WindowsPlayer_Data", "globalgamemanagers.assets");

            switch (phase)
            {
                case "inventory":
                    return RunInventory(bundlesDir);
                case "names":
                    return RunNames(bundlesDir, ggmPath);
                case "dumpbundle":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("usage: dumpbundle <filename-substring>");
                        return 2;
                    }
                    return RunDumpBundle(bundlesDir, args[1]);
                case "ggm":
                    return RunGgm(ggmPath);
                case "scenes":
                    return RunScenes(pgInstall);
                case "strings":
                    return RunStrings(pgInstall);
                case "scanall":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("usage: scanall <substring>");
                        return 2;
                    }
                    return RunScanAll(pgInstall, args[1]);
                default:
                    Console.WriteLine($"unknown phase '{phase}'. expected: inventory | names | dumpbundle | ggm");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("=== PROBE FAIL ===");
            Console.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int RunInventory(string bundlesDir)
    {
        // Build a per-bundle asset-type histogram. Output is wide; pipe to a
        // file. The goal: spot bundles whose composition is *not* "1 Texture2D"
        // — anything carrying many MonoBehaviours / ScriptableObjects / Sprites
        // is a candidate for the `names` + `dumpbundle` phases.
        var bundles = Directory.EnumerateFiles(bundlesDir, "*.bundle").OrderBy(p => p).ToList();
        Console.WriteLine($"[inventory] {bundles.Count} bundles in {bundlesDir}");
        Console.WriteLine();
        Console.WriteLine("name\ttex2d\tsprite\tmono\tgo\tscriptobj\tother\ttotal");

        var manager = new AssetsManager();
        foreach (var bundlePath in bundles)
        {
            try
            {
                var bunInst = manager.LoadBundleFile(bundlePath, true);
                var bundleFile = bunInst.file;
                var entries = bundleFile.BlockAndDirInfo?.DirectoryInfos?.Count ?? 0;
                var tex2d = 0; var sprite = 0; var mono = 0; var go = 0; var so = 0; var other = 0; var total = 0;

                for (int idx = 0; idx < entries; idx++)
                {
                    AssetsFileInstance? afileInst = null;
                    try { afileInst = manager.LoadAssetsFileFromBundle(bunInst, idx); }
                    catch { continue; }
                    if (afileInst?.file?.AssetInfos is null) continue;
                    foreach (var info in afileInst.file.AssetInfos)
                    {
                        total++;
                        switch ((AssetClassID)info.TypeId)
                        {
                            case AssetClassID.Texture2D: tex2d++; break;
                            case AssetClassID.Sprite: sprite++; break;
                            case AssetClassID.MonoBehaviour: mono++; break;
                            case AssetClassID.GameObject: go++; break;
                            case AssetClassID.MonoScript: so++; break; // misnamed bucket — see note in `names`
                            default: other++; break;
                        }
                    }
                }
                manager.UnloadAll(true);

                Console.WriteLine($"{Path.GetFileName(bundlePath)}\t{tex2d}\t{sprite}\t{mono}\t{go}\t{so}\t{other}\t{total}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Path.GetFileName(bundlePath)}\tERR\t{ex.GetType().Name}: {ex.Message}");
                if (Environment.GetEnvironmentVariable("PROBE_STACK") == "1")
                {
                    Console.WriteLine(ex.StackTrace);
                }
            }
        }
        return 0;
    }

    private static int RunNames(string bundlesDir, string ggmPath)
    {
        var bundles = Directory.EnumerateFiles(bundlesDir, "*.bundle").OrderBy(p => p).ToList();
        Console.WriteLine($"[names] scanning {bundles.Count} bundles for keywords:");
        Console.WriteLine($"        {string.Join(", ", IconKeywords)}");
        Console.WriteLine();

        var manager = new AssetsManager();
        var totalHits = 0;
        foreach (var bundlePath in bundles)
        {
            try
            {
                var bunInst = manager.LoadBundleFile(bundlePath, true);
                var entries = bunInst.file.BlockAndDirInfo?.DirectoryInfos?.Count ?? 0;
                var bundleName = Path.GetFileName(bundlePath);
                for (int idx = 0; idx < entries; idx++)
                {
                    AssetsFileInstance? afileInst = null;
                    try { afileInst = manager.LoadAssetsFileFromBundle(bunInst, idx); }
                    catch { continue; }
                    if (afileInst?.file?.AssetInfos is null) continue;
                    foreach (var info in afileInst.file.AssetInfos)
                    {
                        string? name = TryGetAssetName(manager, afileInst, info);
                        if (name is null) continue;
                        var lower = name.ToLowerInvariant();
                        if (!IconKeywords.Any(k => lower.Contains(k))) continue;
                        Console.WriteLine($"{bundleName}\t{(AssetClassID)info.TypeId}\tpathId={info.PathId}\tname={name}");
                        totalHits++;
                    }
                }
                manager.UnloadAll(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Path.GetFileName(bundlePath)}\tERR\t{ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"[names] total bundle hits: {totalHits}");
        Console.WriteLine();
        Console.WriteLine($"[names] scanning {ggmPath}");
        if (File.Exists(ggmPath))
        {
            try
            {
                var inst = manager.LoadAssetsFile(ggmPath, false);
                var ggmHits = 0;
                foreach (var info in inst.file.AssetInfos)
                {
                    string? name = TryGetAssetName(manager, inst, info);
                    if (name is null) continue;
                    var lower = name.ToLowerInvariant();
                    if (!IconKeywords.Any(k => lower.Contains(k))) continue;
                    Console.WriteLine($"ggm\t{(AssetClassID)info.TypeId}\tpathId={info.PathId}\tname={name}");
                    ggmHits++;
                }
                Console.WriteLine($"[names] ggm hits: {ggmHits}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ggm ERR: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("[names] globalgamemanagers.assets not found");
        }

        return 0;
    }

    private static int RunDumpBundle(string bundlesDir, string substring)
    {
        var matches = Directory.EnumerateFiles(bundlesDir, "*.bundle")
            .Where(p => Path.GetFileName(p).Contains(substring, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p)
            .ToList();
        if (matches.Count == 0)
        {
            Console.WriteLine($"[dumpbundle] no bundle filename contains '{substring}'");
            return 1;
        }
        Console.WriteLine($"[dumpbundle] {matches.Count} matches; dumping first: {Path.GetFileName(matches[0])}");

        var manager = new AssetsManager();
        var bunInst = manager.LoadBundleFile(matches[0], true);
        var entries = bunInst.file.BlockAndDirInfo?.DirectoryInfos?.Count ?? 0;
        for (int idx = 0; idx < entries; idx++)
        {
            AssetsFileInstance? afileInst = null;
            try { afileInst = manager.LoadAssetsFileFromBundle(bunInst, idx); }
            catch (Exception ex) { Console.WriteLine($"entry {idx}: load fail {ex.Message}"); continue; }
            if (afileInst?.file?.AssetInfos is null)
            {
                Console.WriteLine($"-- entry {idx}: not an assets file (resS / metadata)");
                continue;
            }
            Console.WriteLine($"-- entry {idx}: {afileInst.file.AssetInfos.Count} assets, unity={afileInst.file.Metadata.UnityVersion}");
            foreach (var info in afileInst.file.AssetInfos)
            {
                var classId = (AssetClassID)info.TypeId;
                var typeName = classId.ToString();
                string? name = TryGetAssetName(manager, afileInst, info);
                Console.WriteLine($"  {typeName}\tpathId={info.PathId}\tname={name ?? "<no m_Name>"}");
                // Dump the field tree for every type that might carry structured
                // data — Sprite (m_Rect / m_Pivot / m_RD), MonoBehaviour (serialized
                // C# fields), ScriptableObject (also surfaces as MonoBehaviour), and
                // GameObject (component listing).
                if (classId is AssetClassID.MonoBehaviour or AssetClassID.MonoScript
                            or AssetClassID.Sprite or AssetClassID.GameObject
                            or AssetClassID.AssetBundle)
                {
                    DumpFieldTree(manager, afileInst, info, indent: "    ");
                }
            }
        }
        return 0;
    }

    private static int RunGgm(string ggmPath)
    {
        if (!File.Exists(ggmPath))
        {
            Console.WriteLine($"[ggm] not found: {ggmPath}");
            return 1;
        }
        var manager = new AssetsManager();
        var inst = manager.LoadAssetsFile(ggmPath, false);
        Console.WriteLine($"[ggm] {inst.file.AssetInfos.Count} assets, unity={inst.file.Metadata.UnityVersion}");
        var histogram = new SortedDictionary<string, int>();
        foreach (var info in inst.file.AssetInfos)
        {
            var t = ((AssetClassID)info.TypeId).ToString();
            histogram[t] = histogram.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        Console.WriteLine("[ggm] type histogram:");
        foreach (var kv in histogram) Console.WriteLine($"  {kv.Key}: {kv.Value}");
        Console.WriteLine();
        // MonoScripts in ggm have empty m_Name; their identity is in m_ClassName +
        // m_Namespace + m_AssemblyName. Dump all of those so we can grep for any
        // map/landmark/icon/minimap C# type that exists in the PG codebase. Zero
        // hits → PG doesn't have data-driven map-icon classes at all.
        Console.WriteLine("[ggm] MonoScript class names:");
        var failures = 0;
        var emitted = 0;
        foreach (var info in inst.file.AssetInfos)
        {
            if ((AssetClassID)info.TypeId != AssetClassID.MonoScript) continue;
            try
            {
                var f = manager.GetBaseField(inst, info);
                if (f is null) { failures++; continue; }
                string cls = ReadStringField(f, "m_ClassName");
                string ns = ReadStringField(f, "m_Namespace");
                string asm = ReadStringField(f, "m_AssemblyName");
                Console.WriteLine($"  pathId={info.PathId}\t{asm}\t{ns}.{cls}");
                emitted++;
            }
            catch (Exception ex)
            {
                failures++;
                if (failures <= 3)
                {
                    Console.WriteLine($"  pathId={info.PathId}\tERR\t{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        Console.WriteLine($"[ggm] emitted {emitted}, failures {failures}");
        return 0;
    }

    private static string ReadStringField(AssetTypeValueField root, string name)
    {
        var f = root[name];
        if (f is null || f.IsDummy) return "";
        try { return f.AsString ?? ""; }
        catch { return ""; }
    }

    private static int RunScenes(string pgInstall)
    {
        // Each level<N> file is a serialized Unity scene. They hold scene
        // GameObjects + their components. We don't try to introspect them
        // (would need the user-script type tree); we just look at named asset
        // hits + type histogram. If a scene contains a "MapIcon"-class
        // MonoBehaviour, the m_Name typically carries the class name in the
        // scene serialization.
        var dataDir = Path.Combine(pgInstall, "WindowsPlayer_Data");
        var scenes = Directory.EnumerateFiles(dataDir, "level*")
            .Where(p => !p.EndsWith(".resS", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p)
            .ToList();
        Console.WriteLine($"[scenes] {scenes.Count} level files in {dataDir}");
        var manager = new AssetsManager();
        var totalHits = 0;
        foreach (var scenePath in scenes)
        {
            AssetsFileInstance? inst = null;
            try { inst = manager.LoadAssetsFile(scenePath, false); }
            catch (Exception ex) { Console.WriteLine($"{Path.GetFileName(scenePath)}\tERR\t{ex.Message}"); continue; }
            if (inst?.file?.AssetInfos is null) continue;

            var assets = inst.file.AssetInfos.Count;
            var histogram = new SortedDictionary<string, int>();
            var hitsHere = 0;
            foreach (var info in inst.file.AssetInfos)
            {
                var t = ((AssetClassID)info.TypeId).ToString();
                histogram[t] = histogram.TryGetValue(t, out var c) ? c + 1 : 1;
                var name = TryGetAssetName(manager, inst, info);
                if (name is null) continue;
                var lower = name.ToLowerInvariant();
                if (!IconKeywords.Any(k => lower.Contains(k))) continue;
                Console.WriteLine($"{Path.GetFileName(scenePath)}\t{(AssetClassID)info.TypeId}\tpathId={info.PathId}\tname={name}");
                hitsHere++;
                totalHits++;
            }
            var topTypes = string.Join(", ", histogram.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"[scenes] {Path.GetFileName(scenePath)}: {assets} assets, hits={hitsHere}, top: {topTypes}");
            manager.UnloadAll(true);
        }
        Console.WriteLine($"[scenes] total hits across {scenes.Count} scenes: {totalHits}");
        return 0;
    }

    private static int RunStrings(string pgInstall)
    {
        // Brute-force fallback: extract printable-ASCII runs from ggm +
        // globalgamemanagers (engine config) + the IL2CPP metadata + one
        // sample scene, and filter for class/symbol names hinting at map
        // icons. Catches MonoScript classNames in ggm that AssetsTools.NET
        // couldn't reflect without a classdata.tpk for Unity 6000.3.
        string[] keywords = [
            "Landmark", "MinimapIcon", "MapIcon", "MapMarker", "Waypoint",
            "MapData", "MapMeta", "MapRef", "MapPoi", "MapNpc",
            "AreaMap", "WorldMap", "MapController", "MapView",
            "MapManager", "MapDb",
        ];
        var targets = new List<string>
        {
            Path.Combine(pgInstall, "WindowsPlayer_Data", "globalgamemanagers.assets"),
            Path.Combine(pgInstall, "WindowsPlayer_Data", "globalgamemanagers"),
            Path.Combine(pgInstall, "WindowsPlayer_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            Path.Combine(pgInstall, "WindowsPlayer_Data", "level1"),
        };
        foreach (var path in targets)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"[strings] skip (missing): {path}");
                continue;
            }
            var bytes = File.ReadAllBytes(path);
            Console.WriteLine($"[strings] scanning {path} ({bytes.Length:N0} bytes)");
            var hits = new SortedSet<string>(StringComparer.Ordinal);
            int runStart = -1;
            for (int i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                if (b >= 32 && b < 127)
                {
                    if (runStart < 0) runStart = i;
                }
                else
                {
                    if (runStart >= 0 && i - runStart >= 6)
                    {
                        var s = System.Text.Encoding.ASCII.GetString(bytes, runStart, i - runStart);
                        foreach (var kw in keywords)
                        {
                            if (s.Contains(kw, StringComparison.OrdinalIgnoreCase))
                            {
                                hits.Add(s);
                                break;
                            }
                        }
                    }
                    runStart = -1;
                }
            }
            foreach (var h in hits) Console.WriteLine($"  {h}");
            Console.WriteLine($"  [strings] {hits.Count} hits in {Path.GetFileName(path)}");
        }
        return 0;
    }

    private static int RunScanAll(string pgInstall, string substring)
    {
        // Brute-force byte-string scan of every bundle + every scene file +
        // ggm. Counts how many files contain the substring and reports them.
        // Cheap: 4GB+ of data but linear sequential read.
        var needle = System.Text.Encoding.ASCII.GetBytes(substring);
        var roots = new List<string>
        {
            Path.Combine(pgInstall, "WindowsPlayer_Data", "StreamingAssets", "aa", "StandaloneWindows64"),
        };
        var single = new List<string>
        {
            Path.Combine(pgInstall, "WindowsPlayer_Data", "globalgamemanagers.assets"),
            Path.Combine(pgInstall, "WindowsPlayer_Data", "globalgamemanagers"),
        };
        var files = new List<string>(single);
        foreach (var r in roots)
        {
            if (Directory.Exists(r)) files.AddRange(Directory.EnumerateFiles(r));
        }
        var levelDir = Path.Combine(pgInstall, "WindowsPlayer_Data");
        if (Directory.Exists(levelDir))
        {
            files.AddRange(Directory.EnumerateFiles(levelDir, "level*")
                .Where(p => !p.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)));
        }

        Console.WriteLine($"[scanall] needle='{substring}' across {files.Count} files");
        var hits = 0;
        foreach (var path in files)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch { continue; }
            if (IndexOf(bytes, needle) >= 0)
            {
                Console.WriteLine($"  HIT  {Path.GetFileName(path)}  ({bytes.Length:N0} bytes)");
                hits++;
            }
        }
        Console.WriteLine($"[scanall] {hits} files contain '{substring}'");
        return 0;
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        if (needle.Length == 0 || hay.Length < needle.Length) return -1;
        var last = hay.Length - needle.Length;
        for (int i = 0; i <= last; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (hay[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    private static string? TryGetAssetName(AssetsManager manager, AssetsFileInstance afileInst, AssetFileInfo info)
    {
        try
        {
            var field = manager.GetBaseField(afileInst, info);
            if (field is null) return null;
            var nameField = field["m_Name"];
            if (nameField is null || nameField.IsDummy) return null;
            return nameField.AsString;
        }
        catch
        {
            return null;
        }
    }

    private static void DumpFieldTree(AssetsManager manager, AssetsFileInstance afileInst, AssetFileInfo info, string indent, int maxDepth = 3)
    {
        AssetTypeValueField? root;
        try { root = manager.GetBaseField(afileInst, info); }
        catch (Exception ex) { Console.WriteLine($"{indent}<field-tree fail: {ex.Message}>"); return; }
        if (root is null) { Console.WriteLine($"{indent}<no base field>"); return; }
        DumpField(root, indent, depth: 0, maxDepth);
    }

    private static void DumpField(AssetTypeValueField field, string indent, int depth, int maxDepth)
    {
        if (depth > maxDepth) { Console.WriteLine($"{indent}…(truncated)"); return; }
        var name = field.FieldName ?? "<noname>";
        var type = field.TypeName ?? "<notype>";

        if (field.Children.Count == 0)
        {
            string val;
            try
            {
                val = field.Value?.AsObject?.ToString() ?? "<null>";
            }
            catch { val = "<err>"; }
            if (val.Length > 80) val = val[..80] + "…";
            Console.WriteLine($"{indent}{name}: {type} = {val}");
        }
        else
        {
            Console.WriteLine($"{indent}{name}: {type}");
            foreach (var child in field.Children)
            {
                DumpField(child, indent + "  ", depth + 1, maxDepth);
            }
        }
    }

    private static string ResolvePgInstall()
    {
        var steamRoot = FindSteamRoot();
        var install = FindPg(steamRoot)
            ?? throw new InvalidOperationException($"AppID {PgAppId} not found in any Steam library.");
        if (!Directory.Exists(install))
        {
            throw new DirectoryNotFoundException($"PG install path missing: {install}");
        }
        return install;
    }

    private static string FindSteamRoot()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = hklm.OpenSubKey(@"SOFTWARE\Valve\Steam");
        if (key?.GetValue("InstallPath") is string p && Directory.Exists(p)) return p;
        using var cu = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
        if (cu?.GetValue("SteamPath") is string cp && Directory.Exists(cp.Replace('/', '\\'))) return cp.Replace('/', '\\');
        throw new InvalidOperationException("Steam install path not found.");
    }

    private static string? FindPg(string steamRoot)
    {
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) return null;
        var vdf = File.ReadAllText(vdfPath);
        // We only need to find the library path whose apps block lists PgAppId.
        // Reuse a minimal scanner: walk "path" lines and "apps" blocks. This is
        // a lift of the predecessor MapAssetSpike VDF parser, simplified for
        // probe purposes.
        var tokens = TokenizeVdf(vdf);
        int i = 0;
        while (i < tokens.Count && tokens[i] != "{") i++;
        if (i == tokens.Count) return null;
        i++;
        while (i < tokens.Count && tokens[i] != "}")
        {
            i++; // lib index key
            if (i >= tokens.Count || tokens[i] != "{") break;
            i++;
            string? path = null;
            var appIds = new HashSet<int>();
            while (i < tokens.Count && tokens[i] != "}")
            {
                var key = tokens[i++];
                if (i >= tokens.Count) break;
                if (key == "path") path = tokens[i++];
                else if (key == "apps" && tokens[i] == "{")
                {
                    i++;
                    while (i < tokens.Count && tokens[i] != "}")
                    {
                        var k = tokens[i++];
                        if (i >= tokens.Count) break;
                        i++;
                        if (int.TryParse(k, out var id)) appIds.Add(id);
                    }
                    if (i < tokens.Count) i++;
                }
                else
                {
                    if (tokens[i] == "{") { int depth = 1; i++; while (i < tokens.Count && depth > 0) { if (tokens[i] == "{") depth++; else if (tokens[i] == "}") depth--; i++; } }
                    else i++;
                }
            }
            if (i < tokens.Count) i++;
            if (path != null && appIds.Contains(PgAppId))
            {
                var candidate = Path.Combine(path, "steamapps", "common", "Project Gorgon");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static List<string> TokenizeVdf(string text)
    {
        var t = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '{' || c == '}') { t.Add(c.ToString()); i++; continue; }
            if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        var esc = text[i + 1];
                        sb.Append(esc switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => esc });
                        i += 2;
                    }
                    else { sb.Append(text[i]); i++; }
                }
                t.Add(sb.ToString());
                if (i < text.Length) i++;
                continue;
            }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}') i++;
            t.Add(text[start..i]);
        }
        return t;
    }
}
