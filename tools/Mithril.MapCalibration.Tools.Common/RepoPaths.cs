namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Resolves canonical in-repo asset paths the map-calibration tools share —
/// bundled landmarks/NPCs reference data, the calibration baseline JSON, the
/// per-tool scratch directories for extracted map textures + icon templates.
///
/// <para>Both the CLI (<c>MapCalibrationFromScreenshot</c>) and the WPF
/// workspace (<c>MapCalibrationWpf</c>) need to find these files identically
/// — the tools have to agree on paths because they read/write the same on-disk
/// artifacts (extracted PNGs are cached, baseline JSON is the persistence
/// surface). Centralising the resolution here keeps the two callers in sync.</para>
///
/// <para>The repo-relative paths are resolved by walking up from
/// <see cref="AppContext.BaseDirectory"/> until a folder containing
/// <c>Mithril.slnx</c> is found — same approach <c>Pipeline.cs</c> used before
/// this helper landed. Throws <see cref="UserFacingException"/> when the walk
/// fails so the caller can surface a useful error.</para>
/// </summary>
public static class RepoPaths
{
    /// <summary>
    /// Absolute path to <c>src/Mithril.Shared/Reference/BundledData/landmarks.json</c>.
    /// </summary>
    public static string LandmarksJsonPath() =>
        EnsureExists(Path.Combine(RepoRoot(), "src", "Mithril.Shared", "Reference", "BundledData", "landmarks.json"));

    /// <summary>
    /// Absolute path to <c>src/Mithril.Shared/Reference/BundledData/npcs.json</c>.
    /// </summary>
    public static string NpcsJsonPath() =>
        EnsureExists(Path.Combine(RepoRoot(), "src", "Mithril.Shared", "Reference", "BundledData", "npcs.json"));

    /// <summary>
    /// Absolute path to <c>src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json</c>.
    /// </summary>
    public static string BaselineJsonPath() =>
        EnsureExists(Path.Combine(RepoRoot(), "src", "Mithril.MapCalibration", "BundledData", "map-calibration-baseline.json"));

    /// <summary>
    /// Scratch directory for cached map-area PNGs (one per area). Matches the
    /// CLI's <c>DefaultScratch("maps")</c> location so the WPF tool reuses any
    /// extraction the CLI already performed (and vice versa).
    /// </summary>
    public static string DefaultMapsCacheDir() => DefaultScratch("maps");

    /// <summary>
    /// Scratch directory for icon templates extracted from sharedassets0.assets.
    /// Matches the CLI's <c>DefaultScratch("icons")</c> location.
    /// </summary>
    public static string DefaultIconsCacheDir() => DefaultScratch("icons");

    /// <summary>
    /// Default classdata.tpk path the CLI uses (alongside the tool's binary).
    /// The icon extractor errors with a friendly download URL when the file
    /// is missing from this path.
    /// </summary>
    public static string DefaultTpkPath() =>
        Path.Combine(AppContext.BaseDirectory, "classdata.tpk");

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> looking for the
    /// folder that owns <c>Mithril.slnx</c>. Throws if the repo root can't be
    /// found within 10 levels (which is more than enough — the tool bin paths
    /// are 5 levels deep).
    /// </summary>
    public static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "Mithril.slnx"))) return dir;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new UserFacingException(
            $"could not locate Mithril.slnx walking up from {AppContext.BaseDirectory}");
    }

    private static string DefaultScratch(string sub)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mithril-852", sub);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string EnsureExists(string path)
    {
        if (!File.Exists(path))
        {
            throw new UserFacingException($"bundled asset not found: {path}");
        }
        return path;
    }
}
