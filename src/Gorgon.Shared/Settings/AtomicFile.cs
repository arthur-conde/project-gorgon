using System.IO;

namespace Gorgon.Shared.Settings;

/// <summary>Filesystem helpers that absorb the Windows-specific flake you hit when
/// Defender / Search indexer briefly opens — or worse, deletes — a freshly closed
/// scratch file in <c>%TEMP%</c>.</summary>
/// <remarks>
/// <para>The naive write-tmp-then-move sequence is two-step. Defender can intervene
/// at either step:</para>
/// <list type="bullet">
///   <item><b>Lock</b> the closed <c>.tmp</c> for inline scanning, which makes
///   <see cref="File.Move(string, string, bool)"/> throw <see cref="UnauthorizedAccessException"/>
///   or <see cref="IOException"/>.</item>
///   <item><b>Quarantine / delete</b> the <c>.tmp</c> outright, which makes the move
///   throw <see cref="FileNotFoundException"/>. Retrying just the move is useless —
///   there is nothing to rename.</item>
/// </list>
/// <para>The helper therefore retries the WHOLE sequence (re-create the tmp, then
/// move) on any of those exceptions, with a generous backoff. Happy-path callers
/// pay nothing.</para>
/// </remarks>
internal static class AtomicFile
{
    // 0, 50, 100, 200, 400, 800, 1600ms — total worst-case wait ~3.15s.
    // Generous tail to absorb Defender quarantine-then-restore cycles observed on
    // GitHub Actions windows-latest runners.
    private static readonly int[] BackoffMs = [0, 50, 100, 200, 400, 800, 1600];

    /// <summary>Write <paramref name="payload"/> to a sibling <c>.tmp</c> and rename
    /// over <paramref name="destination"/>, retrying the whole sequence on
    /// AV/indexer-induced sharing violations or scratch-file deletions.</summary>
    public static void WriteAllBytesAtomic(string destination, byte[] payload)
    {
        EnsureDirectory(destination);
        // Avoid the `.tmp` extension — it triggers Defender ATR heuristics on Windows
        // that scan-and-quarantine "ransomware-shaped" temp files in user paths.
        // `.partial` is uncommon enough to slip past those rules while still being
        // descriptive to a human inspecting the directory mid-write.
        var tmp = destination + ".partial";

        for (var attempt = 0; attempt < BackoffMs.Length; attempt++)
        {
            if (BackoffMs[attempt] > 0) Thread.Sleep(BackoffMs[attempt]);
            try
            {
                File.WriteAllBytes(tmp, payload);
                File.Move(tmp, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < BackoffMs.Length - 1)
            {
                // Fall through to next attempt — re-create the tmp from scratch.
            }
        }
    }

    /// <summary>Async variant. Re-creates the tmp on each retry from the same payload.</summary>
    public static async Task WriteAllBytesAtomicAsync(string destination, byte[] payload, CancellationToken ct = default)
    {
        EnsureDirectory(destination);
        // Avoid the `.tmp` extension — it triggers Defender ATR heuristics on Windows
        // that scan-and-quarantine "ransomware-shaped" temp files in user paths.
        // `.partial` is uncommon enough to slip past those rules while still being
        // descriptive to a human inspecting the directory mid-write.
        var tmp = destination + ".partial";

        for (var attempt = 0; attempt < BackoffMs.Length; attempt++)
        {
            if (BackoffMs[attempt] > 0) await Task.Delay(BackoffMs[attempt], ct).ConfigureAwait(false);
            try
            {
                await File.WriteAllBytesAsync(tmp, payload, ct).ConfigureAwait(false);
                File.Move(tmp, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < BackoffMs.Length - 1)
            {
                // Fall through.
            }
        }
    }

    /// <summary>Serialize the payload into memory, then atomically write+move with retry.
    /// In-memory serialization is intentional: each retry attempt re-creates the tmp from
    /// the same bytes, so we can't stream into the tmp directly.</summary>
    public static async Task WriteJsonAtomicAsync<T>(
        string destination,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await System.Text.Json.JsonSerializer.SerializeAsync(ms, value, typeInfo, ct).ConfigureAwait(false);
        await WriteAllBytesAtomicAsync(destination, ms.ToArray(), ct).ConfigureAwait(false);
    }

    public static void WriteJsonAtomic<T>(
        string destination,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        using var ms = new MemoryStream();
        System.Text.Json.JsonSerializer.Serialize(ms, value, typeInfo);
        WriteAllBytesAtomic(destination, ms.ToArray());
    }

    private static bool IsTransient(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex is FileNotFoundException
        || ex is IOException;

    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }
}
