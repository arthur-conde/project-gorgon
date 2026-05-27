using System.Globalization;
using System.Runtime.CompilerServices;
using Arda.Abstractions.Logs;
using Arda.Ingest.Classification;
using Arda.Ingest.Clock;
using Arda.Ingest.Tailer;

namespace Arda.Ingest.Coordinator;

/// <summary>
/// Source coordinator for the Chat log family (Chat-yy-mm-dd.log files).
/// Implements <see cref="ILogLineSource"/> by enumerating date-ordered chat
/// log files in the <c>ChatLogs/</c> directory and tailing the most recent.
/// <para>
/// Responsibilities:
/// <list type="bullet">
///   <item><b>Directory enumeration.</b> Scans <c>ChatLogs/</c> for files
///   matching <c>Chat-yy-mm-dd.log</c>. Files are processed in filename
///   order (lexicographic sort is chronological for this format).</item>
///   <item><b>Midnight rollover.</b> When a new chat file appears (the date
///   rolled over), the coordinator finishes the current file and switches to
///   the new one.</item>
///   <item><b>Session-bounded replay.</b> Replays from the most recent login
///   banner forward (principle 9), not the entire <c>ChatLogs/</c> archive.</item>
///   <item><b>IsReplay stamping.</b> Historical files are replayed with
///   <c>IsReplay = true</c>. When the coordinator reaches the tail of the
///   most recent file, <c>IsReplay</c> flips to <c>false</c>.</item>
///   <item><b>Single active tailer.</b> Only one file handle is open at a
///   time. Historical files are read to completion and closed before the
///   next opens.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class ChatLogSource : ILogLineSource
{
    private readonly string _chatLogDirectory;
    private readonly TimeProvider _time;
    private readonly ChatLogClock _clock;
    private readonly BatchProcessor _processor;
    private readonly TimeSpan _pollInterval;

    private bool _reachedLive;

    public ChatLogSource(
        string chatLogDirectory,
        TimeProvider time,
        TimeSpan? pollInterval = null)
    {
        _chatLogDirectory = chatLogDirectory ?? throw new ArgumentNullException(nameof(chatLogDirectory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _clock = new ChatLogClock();
        _processor = new BatchProcessor(new LineClassifier(_clock), time);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<LogLine> Lines(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var files = GetOrderedChatFiles();
        var (startFileIndex, startOffset) = ChatSessionStartScanner.ResolveSessionStart(files);

        // Replay from session-start banner through files before the live tail.
        for (var i = startFileIndex; i < files.Length - 1 && !ct.IsCancellationRequested; i++)
        {
            var tailer = new LogSourceTailer(files[i]);
            if (i == startFileIndex && startOffset > 0)
                tailer.Offset = startOffset;

            while (!ct.IsCancellationRequested)
            {
                var results = _processor.ProcessBatch(tailer, isReplay: true);
                if (results is null) break;

                foreach (var line in results)
                {
                    TryApplyBannerOffset(line.Log);
                    yield return line;
                }
            }
        }

        if (files.Length == 0)
        {
            while (!ct.IsCancellationRequested)
            {
                files = GetOrderedChatFiles();
                if (files.Length > 0) break;
                await Task.Delay(_pollInterval, _time, ct).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) yield break;
        }

        // Tail the most recent file (live).
        var currentFile = files[^1];
        var liveTailer = new LogSourceTailer(currentFile);
        if (startFileIndex == files.Length - 1 && startOffset > 0)
            liveTailer.Offset = startOffset;

        while (!ct.IsCancellationRequested)
        {
            var results = _processor.ProcessBatch(liveTailer, isReplay: !_reachedLive);

            // Transition to live: flip once the tailer's offset reaches EOF.
            // Checked after every read (not only on empty batches) so that
            // IsReplay transitions correctly even if data arrives every cycle.
            if (!_reachedLive && liveTailer.HasCaughtUp)
                _reachedLive = true;

            if (results is null)
            {
                // Check if a new file appeared (midnight rollover).
                var latestFiles = GetOrderedChatFiles();
                if (latestFiles.Length > 0 && latestFiles[^1] != currentFile)
                {
                    currentFile = latestFiles[^1];
                    liveTailer = new LogSourceTailer(currentFile);
                }

                await Task.Delay(_pollInterval, _time, ct).ConfigureAwait(false);
                continue;
            }

            foreach (var line in results)
            {
                TryApplyBannerOffset(line.Log);
                yield return line;
            }
        }
    }

    // "Timezone Offset " is 16 chars; the value is -?H:mm:ss or -?HH:mm:ss (8–9 chars + trailing '.')
    private const string TimezoneOffsetMarker = "Timezone Offset ";

    /// <summary>
    /// If the line is a chat login banner, extract the timezone offset and
    /// apply it to the clock. This is an L0 concern: the clock needs the
    /// offset before subsequent lines are classified. The banner line itself
    /// uses the prior offset (acceptable — it's the first line of a session).
    /// </summary>
    private void TryApplyBannerOffset(string log)
    {
        if (log.Length < 5 || log[0] != '*')
            return;

        var span = log.AsSpan();
        var markerIdx = span.IndexOf(TimezoneOffsetMarker.AsSpan(), StringComparison.Ordinal);
        if (markerIdx < 0)
            return;

        var offsetStart = markerIdx + TimezoneOffsetMarker.Length;
        var remaining = span[offsetStart..];

        // Strip trailing '.' if present
        if (remaining.Length > 0 && remaining[^1] == '.')
            remaining = remaining[..^1];

        if (TryParseOffset(remaining, out var offset))
            _clock.SetOffset(offset);
    }

    private static bool TryParseOffset(ReadOnlySpan<char> s, out TimeSpan result)
    {
        result = default;

        var negative = false;
        if (s.Length > 0 && s[0] == '-')
        {
            negative = true;
            s = s[1..];
        }

        // Expected: H:mm:ss or HH:mm:ss
        var firstColon = s.IndexOf(':');
        if (firstColon < 1) return false;

        if (!int.TryParse(s[..firstColon], NumberStyles.None, CultureInfo.InvariantCulture, out var hours))
            return false;

        s = s[(firstColon + 1)..];
        if (s.Length < 5 || s[2] != ':') return false;

        if (!int.TryParse(s[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            return false;
        if (!int.TryParse(s[3..5], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return false;

        result = new TimeSpan(hours, minutes, seconds);
        if (negative) result = result.Negate();
        return true;
    }

    private string[] GetOrderedChatFiles()
    {
        if (!Directory.Exists(_chatLogDirectory))
            return [];

        // The glob "Chat-??-??-??.log" matches non-digit characters too.
        // Post-filter with a structural check on the filename to ensure only
        // properly-named date-based files are processed.
        return Directory.GetFiles(_chatLogDirectory, "Chat-??-??-??.log")
            .Where(IsValidChatFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsValidChatFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).AsSpan();
        // Expected: "Chat-yy-mm-dd" (13 chars)
        if (name.Length != 13) return false;
        return char.IsAsciiDigit(name[5]) && char.IsAsciiDigit(name[6])
            && char.IsAsciiDigit(name[8]) && char.IsAsciiDigit(name[9])
            && char.IsAsciiDigit(name[11]) && char.IsAsciiDigit(name[12]);
    }
}
