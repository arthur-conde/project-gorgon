using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Mithril.Shared.Game;

public static class GameLocator
{
    /// <summary>PG ships as Steam AppID 342940.</summary>
    private const int PgAppId = 342940;

    public static string? AutoDetectGameRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData)) return null;
        var candidate = Path.Combine(
            Directory.GetParent(appData)?.FullName ?? "",
            "LocalLow", "Elder Game", "Project Gorgon");
        return Directory.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Fail-soft auto-detection of the PG Unity/Steam <b>install</b> directory
    /// (e.g. <c>…\steamapps\common\Project Gorgon</c>) — the dir the asset-extractor
    /// sidecar reads via its <c>--install</c> root. This is distinct from
    /// <see cref="AutoDetectGameRoot"/>, which returns the LocalLow <i>data</i> dir
    /// (Player.log etc.). PG is Steam-only on Windows, so detection is Steam-only.
    ///
    /// <para>Ported from <c>tools/Mithril.MapCalibration.Tools.Common/SteamInstall.cs</c>,
    /// but adapted to be <b>fail-soft</b>: every failure (no Steam, no
    /// <c>libraryfolders.vdf</c>, PG not found, path missing, registry/IO exception)
    /// returns <see langword="null"/>. It NEVER throws — the whole body is guarded.</para>
    /// </summary>
    public static string? AutoDetectInstallRoot()
    {
        try
        {
            var steamRoot = FindSteamRoot();
            if (steamRoot is null) return null;

            var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath)) return null;

            var libraryRoots = ParseLibraryRoots(File.ReadAllText(vdfPath));
            return ResolvePgInstall(libraryRoots);
        }
        catch
        {
            // Any unexpected registry / IO / parse failure → safe-degrade to null.
            return null;
        }
    }

    /// <summary>
    /// Fail-soft Steam-root lookup via the registry. Tries <c>HKCU\Software\Valve\Steam</c>
    /// (<c>SteamPath</c>, <c>/</c>→<c>\</c> normalised) first, then the 32-bit view of
    /// <c>HKLM\SOFTWARE\Valve\Steam</c> (<c>InstallPath</c> — the WOW6432Node equivalent).
    /// Returns <see langword="null"/> on any miss or exception.
    /// </summary>
    private static string? FindSteamRoot()
    {
        try
        {
            using var cu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (cu?.GetValue("SteamPath") is string cp)
            {
                cp = cp.Replace('/', '\\');
                if (Directory.Exists(cp)) return cp;
            }
        }
        catch { /* fall through to HKLM */ }

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (key?.GetValue("InstallPath") is string p && Directory.Exists(p))
            {
                return p;
            }
        }
        catch { /* return null below */ }

        return null;
    }

    /// <summary>
    /// Parse a <c>libraryfolders.vdf</c> document into the list of Steam library
    /// roots whose <c>apps</c> block lists PG's appid (<see cref="PgAppId"/>). Pure —
    /// no filesystem or registry access — so it is unit-testable from a sample vdf
    /// string. Order is preserved (first matching library wins downstream).
    /// </summary>
    internal static List<string> ParseLibraryRoots(string vdfText)
    {
        var result = new List<string>();
        var tokens = TokenizeVdf(vdfText);
        int i = 0;
        while (i < tokens.Count && tokens[i] != "{") i++;
        if (i == tokens.Count) return result;
        i++;
        while (i < tokens.Count && tokens[i] != "}")
        {
            i++; // skip the library index key (e.g. "0")
            if (i >= tokens.Count || tokens[i] != "{") break;
            i++;
            string? path = null;
            var appIds = new HashSet<int>();
            while (i < tokens.Count && tokens[i] != "}")
            {
                var k = tokens[i++];
                if (i >= tokens.Count) break;
                if (k == "path")
                {
                    path = tokens[i++];
                }
                else if (k == "apps" && tokens[i] == "{")
                {
                    i++;
                    while (i < tokens.Count && tokens[i] != "}")
                    {
                        var appKey = tokens[i++];
                        if (i >= tokens.Count) break;
                        i++; // skip the app's value
                        if (int.TryParse(appKey, out var id)) appIds.Add(id);
                    }
                    if (i < tokens.Count) i++;
                }
                else if (tokens[i] == "{")
                {
                    int depth = 1; i++;
                    while (i < tokens.Count && depth > 0)
                    {
                        if (tokens[i] == "{") depth++;
                        else if (tokens[i] == "}") depth--;
                        i++;
                    }
                }
                else
                {
                    i++;
                }
            }
            if (i < tokens.Count) i++;
            if (path is not null && appIds.Contains(PgAppId))
            {
                result.Add(path);
            }
        }
        return result;
    }

    /// <summary>
    /// Given candidate Steam library roots, return the first verified PG install dir
    /// (<c>&lt;lib&gt;\steamapps\common\Project Gorgon</c>) — verified by the existence
    /// of <c>WindowsPlayer_Data\StreamingAssets\aa\StandaloneWindows64</c> under it
    /// (the StreamingAssets path is the proof; the appid match upstream is only a
    /// hint). Returns the install dir, not the StreamingAssets subdir, because the
    /// sidecar's <c>--install</c> root expects the install root. Returns
    /// <see langword="null"/> when no candidate verifies.
    /// </summary>
    internal static string? ResolvePgInstall(IEnumerable<string> libraryRoots)
    {
        foreach (var lib in libraryRoots)
        {
            if (string.IsNullOrWhiteSpace(lib)) continue;
            var install = Path.Combine(lib, "steamapps", "common", "Project Gorgon");
            var streamingAssets = Path.Combine(
                install, "WindowsPlayer_Data", "StreamingAssets", "aa", "StandaloneWindows64");
            if (Directory.Exists(streamingAssets)) return install;
        }
        return null;
    }

    /// <summary>Minimal Valve KeyValues tokenizer (quoted strings + braces). Lifted
    /// from the tools <c>SteamInstall</c> so the vdf parse stays byte-identical.</summary>
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
