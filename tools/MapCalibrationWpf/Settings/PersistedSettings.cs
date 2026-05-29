namespace Mithril.Tools.MapCalibrationWpf.Settings;

using System.IO;
using System.Text.Json;

/// <summary>
/// Tool-local persistence for the WPF map-calibration workspace. Backed by a
/// small JSON file at <c>%LocalAppData%/Mithril/MapCalibrationWpf/settings.json</c>.
///
/// <para>Phase 1 carries one field — an override for the Project Gorgon install
/// path used when <c>SteamInstall.FindPgInstall()</c> can't locate the install
/// automatically (custom Steam libraries, non-Steam manual installs). The
/// "5-line JSON write" choice over <c>Properties.Settings</c> is intentional
/// — no designer-tooling dependency, file lives in the same well-known
/// location as the rest of Mithril's settings.</para>
/// </summary>
public sealed record PersistedSettings(string? PgInstallPathOverride)
{
    private static readonly string DefaultPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mithril", "MapCalibrationWpf", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static PersistedSettings Load() => Load(DefaultPath);

    public static PersistedSettings Load(string path)
    {
        if (!File.Exists(path)) return new PersistedSettings((string?)null);
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PersistedSettings>(text, JsonOpts)
                ?? new PersistedSettings((string?)null);
        }
        catch
        {
            // Malformed settings shouldn't brick the tool — fall back to defaults
            // and let the next Save() overwrite the bad file.
            return new PersistedSettings((string?)null);
        }
    }

    public void Save() => Save(DefaultPath);

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }
}
