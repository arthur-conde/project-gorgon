using System.Globalization;
using System.Runtime.CompilerServices;
using Arda.Abstractions.Diagnostics;
using Arda.Abstractions.Logs;
using Arda.Ingest.Classification;
using Arda.Ingest.Clock;
using Arda.Ingest.Tailer;
using Microsoft.Extensions.Logging;

namespace Arda.Ingest.Coordinator;

/// <summary>
/// Source coordinator for the Chat log family (Chat-yy-mm-dd.log files).
/// Implements <see cref="ILogLineSource"/> by enumerating date-ordered chat
/// log files in the <c>ChatLogs/</c> directory and tailing the most recent.
/// </summary>
internal sealed class ChatLogSource : ILogLineSource
{
    private readonly string _chatLogDirectory;
    private readonly TimeProvider _time;
    private readonly ChatLogClock _clock;
    private readonly BatchProcessor _processor;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger? _logger;
    private readonly IIngestPulseSink? _pulseSink;

    private bool _reachedLive;
    private bool _warnedMissingDir;

    public ChatLogSource(
        string chatLogDirectory,
        TimeProvider time,
        TimeSpan? pollInterval = null,
        ILogger? logger = null,
        IIngestPulseSink? pulseSink = null)
    {
        _chatLogDirectory = chatLogDirectory ?? throw new ArgumentNullException(nameof(chatLogDirectory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _clock = new ChatLogClock();
        _processor = new BatchProcessor(new LineClassifier(_clock), time);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        _logger = logger;
        _pulseSink = pulseSink;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<LogLine> Lines(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var files = GetOrderedChatFiles();
        var (startFileIndex, startOffset) = ChatSessionStartScanner.ResolveSessionStart(files);
        _logger?.LogDebug(
            "Chat session replay start at file index {FileIndex} offset {ByteOffset}",
            startFileIndex,
            startOffset);

        for (var i = startFileIndex; i < files.Length - 1 && !ct.IsCancellationRequested; i++)
        {
            _logger?.LogInformation("Replaying chat file {Path}", files[i]);
            var tailer = new LogSourceTailer(files[i], _logger);
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
                if (!Directory.Exists(_chatLogDirectory) && !_warnedMissingDir)
                {
                    _warnedMissingDir = true;
                    _logger?.LogWarning("Chat log directory missing at {Path}", _chatLogDirectory);
                }

                files = GetOrderedChatFiles();
                if (files.Length > 0) break;
                await Task.Delay(_pollInterval, _time, ct).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) yield break;
        }

        var currentFile = files[^1];
        _logger?.LogInformation("Tailing chat file {Path}", currentFile);
        var liveTailer = new LogSourceTailer(currentFile, _logger);
        if (startFileIndex == files.Length - 1 && startOffset > 0)
            liveTailer.Offset = startOffset;

        while (!ct.IsCancellationRequested)
        {
            var results = _processor.ProcessBatch(liveTailer, isReplay: !_reachedLive);

            if (!_reachedLive && liveTailer.HasCaughtUp)
            {
                _reachedLive = true;
                _logger?.LogInformation("Chat log reached live (IsReplay=false)");
            }

            // Pulse on every live-tail iteration including empty reads — the
            // load-bearing signal for WorldHealth drift (#856). A quiet chat
            // channel produces zero domain events but the tailer is still
            // running its poll loop normally; that's "live", not "stalled".
            // (Not pulsed before the file even existed — see the
            // files.Length == 0 waiting loop above. Per #856 design lock #2.)
            _pulseSink?.RecordPoll(
                LogFamily.Chat,
                _time.GetUtcNow(),
                bytesRead: 0,
                linesEmitted: results?.Count ?? 0);

            if (results is null)
            {
                var latestFiles = GetOrderedChatFiles();
                if (latestFiles.Length > 0 && latestFiles[^1] != currentFile)
                {
                    // Drain any final flush PG wrote to the previous-day file
                    // after we detected EOF but before we switched tailers —
                    // chat lines that arrived in that window are otherwise lost.
                    var tailResults = _processor.ProcessBatch(liveTailer, isReplay: !_reachedLive);
                    if (tailResults is not null)
                    {
                        foreach (var line in tailResults)
                        {
                            TryApplyBannerOffset(line.Log);
                            yield return line;
                        }
                    }

                    _logger?.LogInformation(
                        "Chat midnight rollover: {OldPath} → {NewPath}",
                        currentFile,
                        latestFiles[^1]);
                    currentFile = latestFiles[^1];
                    liveTailer = new LogSourceTailer(currentFile, _logger);
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

    private const string TimezoneOffsetMarker = "Timezone Offset ";

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

        return Directory.GetFiles(_chatLogDirectory, "Chat-??-??-??.log")
            .Where(IsValidChatFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsValidChatFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).AsSpan();
        if (name.Length != 13) return false;
        return char.IsAsciiDigit(name[5]) && char.IsAsciiDigit(name[6])
            && char.IsAsciiDigit(name[8]) && char.IsAsciiDigit(name[9])
            && char.IsAsciiDigit(name[11]) && char.IsAsciiDigit(name[12]);
    }
}
