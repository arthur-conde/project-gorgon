using System.IO;

namespace Gorgon.Shared.Settings;

/// <summary>Filesystem helpers that absorb the Windows-specific sharing-violation flake
/// you hit when Defender / Search indexer briefly opens a file just-closed by your code.</summary>
internal static class AtomicFile
{
    /// <summary>
    /// <see cref="File.Move(string, string, bool)"/> with bounded retry for the brief
    /// "Access to the path is denied" / "The process cannot access the file because it is
    /// being used by another process" window that Windows AV and Search indexer create
    /// when they scan a freshly closed file. Retries on <see cref="UnauthorizedAccessException"/>
    /// and <see cref="IOException"/> only; the final attempt rethrows so genuine errors
    /// (target read-only, missing parent dir) still surface.
    /// </summary>
    // 0, 20, 50, 100, 200, 400, 600, 800ms — total worst-case ~2.2s before giving up.
    // The tail is generous because under parallel test load Defender's scan of a freshly
    // closed file can hold the lock for hundreds of ms; happy-path callers pay nothing.
    private static readonly int[] BackoffMs = [0, 20, 50, 100, 200, 400, 600, 800];

    public static void MoveOverwriteWithRetry(string source, string destination)
    {
        for (var attempt = 0; attempt < BackoffMs.Length; attempt++)
        {
            if (BackoffMs[attempt] > 0) Thread.Sleep(BackoffMs[attempt]);
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                (ex is UnauthorizedAccessException || ex is IOException)
                && attempt < BackoffMs.Length - 1)
            {
                // Swallow and retry — AV/indexer briefly held a handle on the source.
            }
        }
    }

    /// <summary>Async variant that <c>await</c>s the backoff so we don't pin a thread.</summary>
    public static async Task MoveOverwriteWithRetryAsync(string source, string destination, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < BackoffMs.Length; attempt++)
        {
            if (BackoffMs[attempt] > 0) await Task.Delay(BackoffMs[attempt], ct).ConfigureAwait(false);
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                (ex is UnauthorizedAccessException || ex is IOException)
                && attempt < BackoffMs.Length - 1)
            {
                // Swallow and retry.
            }
        }
    }
}
