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

        // Process all historical files (all except the last).
        for (var i = 0; i < files.Length - 1 && !ct.IsCancellationRequested; i++)
        {
            var tailer = new LogSourceTailer(files[i]);
            while (!ct.IsCancellationRequested)
            {
                var results = _processor.ProcessBatch(tailer, isReplay: true);
                if (results is null) break;

                foreach (var line in results)
                    yield return line;
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
                yield return line;
        }
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
