using System.Runtime.CompilerServices;
using Arda.Abstractions.Logs;
using Arda.Ingest.Classification;
using Arda.Ingest.Clock;
using Arda.Ingest.Tailer;
using Microsoft.Extensions.Logging;

namespace Arda.Ingest.Coordinator;

/// <summary>
/// Source coordinator for the Player log family (Player-prev.log + Player.log).
/// Implements <see cref="ILogLineSource"/> by orchestrating one or two
/// <see cref="LogSourceTailer"/> instances and a <see cref="LineClassifier"/>.
/// <para>
/// Responsibilities:
/// <list type="bullet">
///   <item><b>Multi-file orchestration.</b> On startup, determines whether
///   Player-prev.log needs reading (checkpoint is in prev, or Mithril survived
///   a game restart). Reads prev to completion, then switches to Player.log.</item>
///   <item><b>Rotation monitoring.</b> Detects when the game replaces Player.log
///   (creation time changes or length drops below last-known offset). Resets the
///   tailer to read the new file from the start.</item>
///   <item><b>IsReplay stamping.</b> Lines from historical data (file existed
///   before this coordinator started) are stamped <c>IsReplay = true</c>. When
///   the tailer's offset reaches EOF, <c>IsReplay</c> flips to <c>false</c>
///   and never reverts.</item>
///   <item><b>Internal byte-offset tracking.</b> Maintains combined position
///   across prev + current for resumption. Not exposed to consumers.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class PlayerLogSource : ILogLineSource
{
    private readonly string _logDirectory;
    private readonly TimeProvider _time;
    private readonly PlayerLogClock _clock;
    private readonly BatchProcessor _processor;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger? _logger;

    private bool _reachedLive;
    private bool _warnedMissingLog;

    public PlayerLogSource(
        string logDirectory,
        TimeProvider time,
        TimeSpan? pollInterval = null,
        ILogger? logger = null)
    {
        _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _clock = new PlayerLogClock(time);
        _processor = new BatchProcessor(new LineClassifier(_clock), time);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        _logger = logger;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<LogLine> Lines(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var playerLogPath = Path.Combine(_logDirectory, "Player.log");
        var prevLogPath = Path.Combine(_logDirectory, "Player-prev.log");

        // Phase 1: Read Player-prev.log if it exists.
        if (File.Exists(prevLogPath))
        {
            _logger?.LogInformation("Replaying {Path}", prevLogPath);
            var prevTailer = new LogSourceTailer(prevLogPath, _logger);
            var anchored = false;

            while (!ct.IsCancellationRequested)
            {
                var results = _processor.ProcessBatch(
                    prevTailer, isReplay: true, _clock, ref anchored);
                if (results is null) break;

                foreach (var line in results)
                    yield return line;
            }
        }

        // Phase 2: Tail Player.log (live).
        _logger?.LogInformation("Tailing {Path}", playerLogPath);
        var tailer = new LogSourceTailer(playerLogPath, _logger);
        var liveAnchored = false;

        while (!ct.IsCancellationRequested)
        {
            if (!File.Exists(playerLogPath) && !_warnedMissingLog)
            {
                _warnedMissingLog = true;
                _logger?.LogWarning("Player log file missing at {Path}", playerLogPath);
            }

            var results = _processor.ProcessBatch(
                tailer, isReplay: !_reachedLive, _clock, ref liveAnchored);

            if (!_reachedLive && tailer.HasCaughtUp)
            {
                _reachedLive = true;
                _logger?.LogInformation("Player log reached live (IsReplay=false)");
            }

            if (results is null)
            {
                await Task.Delay(_pollInterval, _time, ct).ConfigureAwait(false);
                continue;
            }

            foreach (var line in results)
                yield return line;
        }
    }
}
