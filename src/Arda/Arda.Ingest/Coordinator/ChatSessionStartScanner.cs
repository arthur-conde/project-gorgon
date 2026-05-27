namespace Arda.Ingest.Coordinator;

/// <summary>
/// Locates the byte offset of the last PG chat login banner in a file.
/// Used to bound replay to the current session (principle 9) instead of
/// reading every historical <c>Chat-yy-mm-dd.log</c>.
/// </summary>
internal static class ChatSessionStartScanner
{
    private static readonly byte[] Marker = "Logged In As "u8.ToArray();

    /// <summary>
    /// Returns the byte offset of the line start containing the last login banner.
    /// </summary>
    public static bool TryFindLastBannerLineStart(string path, out long lineStartOffset)
    {
        lineStartOffset = 0;
        if (!File.Exists(path))
            return false;

        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var length = fs.Length;
        if (length == 0)
            return false;

        var bytes = new byte[length];
        fs.ReadExactly(bytes);

        var span = bytes.AsSpan();
        long lastLineStart = -1;

        for (var i = 0; i <= span.Length - Marker.Length; i++)
        {
            if (!span.Slice(i, Marker.Length).SequenceEqual(Marker))
                continue;

            var lineStart = i;
            while (lineStart > 0 && span[lineStart - 1] != (byte)'\n')
                lineStart--;

            lastLineStart = lineStart;
        }

        if (lastLineStart < 0)
            return false;

        lineStartOffset = lastLineStart;
        return true;
    }

    /// <summary>
    /// Newest file with a login banner wins; files before that index are skipped.
    /// When no banner exists anywhere, seek the live file to EOF (no replay).
    /// </summary>
    public static (int FileIndex, long ByteOffset) ResolveSessionStart(string[] orderedFiles)
    {
        if (orderedFiles.Length == 0)
            return (0, 0);

        for (var f = orderedFiles.Length - 1; f >= 0; f--)
        {
            if (TryFindLastBannerLineStart(orderedFiles[f], out var offset))
                return (f, offset);
        }

        var latest = orderedFiles[^1];
        var eof = File.Exists(latest) ? new FileInfo(latest).Length : 0L;
        return (orderedFiles.Length - 1, eof);
    }
}
