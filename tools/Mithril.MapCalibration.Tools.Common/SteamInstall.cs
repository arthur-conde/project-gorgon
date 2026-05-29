using System.Text;
using Microsoft.Win32;

namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Locates the user's Steam install and the Project Gorgon application directory.
/// Lifted from <c>tools/MapAssetSpike/Program.cs</c>; PG ships as Steam AppID 342940.
/// Verified once at startup so subsequent code can assume the paths exist.
/// </summary>
public static class SteamInstall
{
    private const int PgAppId = 342940;

    public static string FindPgInstall()
    {
        var steamRoot = FindSteamRoot()
            ?? throw new UserFacingException(
                "Steam install path not found in HKLM\\SOFTWARE\\Valve\\Steam or HKCU\\SOFTWARE\\Valve\\Steam.");
        var pg = FindPg(steamRoot)
            ?? throw new UserFacingException(
                $"Project Gorgon (AppID {PgAppId}) not found in any Steam library under '{steamRoot}'.");
        if (!Directory.Exists(pg))
        {
            throw new UserFacingException($"PG install path missing: {pg}");
        }
        return pg;
    }

    public static string ResolveSharedAssets0(string pgInstall)
    {
        var path = Path.Combine(pgInstall, "WindowsPlayer_Data", "sharedassets0.assets");
        if (!File.Exists(path))
        {
            throw new UserFacingException($"sharedassets0.assets not found at {path}");
        }
        return path;
    }

    public static string ResolveAreaBundleDir(string pgInstall)
    {
        var path = Path.Combine(pgInstall, "WindowsPlayer_Data", "StreamingAssets", "aa", "StandaloneWindows64");
        if (!Directory.Exists(path))
        {
            throw new UserFacingException($"Addressables bundle dir not found at {path}");
        }
        return path;
    }

    private static string? FindSteamRoot()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = hklm.OpenSubKey(@"SOFTWARE\Valve\Steam");
        if (key?.GetValue("InstallPath") is string p && Directory.Exists(p))
        {
            return p;
        }
        using var cu = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
        if (cu?.GetValue("SteamPath") is string cp)
        {
            cp = cp.Replace('/', '\\');
            if (Directory.Exists(cp)) return cp;
        }
        return null;
    }

    private static string? FindPg(string steamRoot)
    {
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) return null;
        var tokens = TokenizeVdf(File.ReadAllText(vdfPath));
        int i = 0;
        while (i < tokens.Count && tokens[i] != "{") i++;
        if (i == tokens.Count) return null;
        i++;
        while (i < tokens.Count && tokens[i] != "}")
        {
            i++;
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
                        i++;
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
