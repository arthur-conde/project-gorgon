using System.Collections.Generic;
using System.IO;

namespace Mithril.Shell.ViewModels;

/// <summary>
/// Per-file deletion helper shared by the Settings → Diagnostics "Clear logs"
/// commands. Deletes the files matched by a set of (directory, glob) pairs,
/// skipping any file currently held open (Serilog opens the live diagnostics
/// file with <c>shared:false</c>; <c>mithril-boot.log</c> and an active perf
/// session may also be locked). Locked / inaccessible files are skipped
/// individually rather than aborting the whole sweep.
/// </summary>
internal static class LogDirectoryCleaner
{
    public readonly record struct CleanResult(int Removed, int Skipped);

    /// <summary>A directory + glob pattern to sweep. Missing directories are ignored.</summary>
    public readonly record struct CleanTarget(string Directory, string Pattern);

    /// <summary>
    /// Attempts to delete every file matched by <paramref name="targets"/>. Returns the
    /// count removed vs. skipped (locked / inaccessible). Never throws for a per-file
    /// failure; enumeration failure of a whole directory is also swallowed (directory
    /// simply contributes nothing).
    /// </summary>
    public static CleanResult Clean(IEnumerable<CleanTarget> targets)
    {
        var removed = 0;
        var skipped = 0;

        foreach (var target in targets)
        {
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(target.Directory))
                    continue;
                files = Directory.EnumerateFiles(target.Directory, target.Pattern, SearchOption.TopDirectoryOnly);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException)
                {
                    // File is held open (e.g. the live mithril-.json, mithril-boot.log,
                    // or an active perf session). Leave it; it clears on next restart.
                    skipped++;
                }
                catch (UnauthorizedAccessException)
                {
                    skipped++;
                }
            }
        }

        return new CleanResult(removed, skipped);
    }
}
