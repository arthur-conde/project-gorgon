using System.Runtime.CompilerServices;
using Arda.Abstractions.Diagnostics;
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
    private readonly IIngestPulseSink? _pulseSink;

    private bool _reachedLive;
    private bool _warnedMissingLog;

    public PlayerLogSource(
        string logDirectory,
        TimeProvider time,
        TimeSpan? pollInterval = null,
        ILogger? logger = null,
        IIngestPulseSink? pulseSink = null)
    {
        _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _clock = new PlayerLogClock(time);
        _processor = new BatchProcessor(new LineClassifier(_clock), time);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        _logger = logger;
        _pulseSink = pulseSink;
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
        DateTime? creationTimeUtc = File.Exists(playerLogPath)
            ? File.GetCreationTimeUtc(playerLogPath)
            : null;

        while (!ct.IsCancellationRequested)
        {
            if (!File.Exists(playerLogPath) && !_warnedMissingLog)
            {
                _warnedMissingLog = true;
                _logger?.LogWarning("Player log file missing at {Path}", playerLogPath);
            }

            // Mid-session rotation: PG crashed/restarted, moved Player.log →
            // Player-prev.log and created a fresh Player.log. If the new file
            // grows past the old offset before this poll, LogSourceTailer's
            // length<offset truncation branch won't fire — we'd silently skip
            // the login banner + ProcessLoadSkills/Recipes burst.
            if (File.Exists(playerLogPath))
            {
                var currentCreation = File.GetCreationTimeUtc(playerLogPath);
                if (creationTimeUtc.HasValue && currentCreation != creationTimeUtc.Value)
                {
                    _logger?.LogInformation(
                        "Player.log rotated mid-session at {Path} (creation {Old:O} → {New:O}); draining prev and reopening",
                        playerLogPath, creationTimeUtc.Value, currentCreation);

                    // The bytes the old tailer was reading were renamed under
                    // us to Player-prev.log, so seek to the old tailer's offset
                    // in prev to pick up any final lines written between our
                    // last poll and the rotation.
                    if (File.Exists(prevLogPath))
                    {
                        var drainTailer = new LogSourceTailer(prevLogPath, _logger)
                        {
                            Offset = tailer.Offset
                        };
                        while (true)
                        {
                            var drained = _processor.ProcessBatch(
                                drainTailer, isReplay: !_reachedLive, _clock, ref liveAnchored);
                            if (drained is null) break;
                            foreach (var line in drained)
                                yield return line;
                        }
                    }

                    _clock.Reset();
                    tailer = new LogSourceTailer(playerLogPath, _logger);
                    liveAnchored = false;
                    _reachedLive = false;
                    creationTimeUtc = currentCreation;
                }
                else if (!creationTimeUtc.HasValue)
                {
                    creationTimeUtc = currentCreation;
                }
            }

            var results = _processor.ProcessBatch(
                tailer, isReplay: !_reachedLive, _clock, ref liveAnchored);

            if (!_reachedLive && tailer.HasCaughtUp)
            {
                _reachedLive = true;
                _logger?.LogInformation("Player log reached live (IsReplay=false)");
            }

            // Pulse on every live-tail iteration, including empty reads. This is
            // the load-bearing signal for WorldHealth drift (issue #856): "the
            // tailer looked at the file" is distinct from "the game wrote a new
            // line". An empty poll on an AFK character is a healthy pulse.
            _pulseSink?.RecordPoll(
                LogFamily.Player,
                _time.GetUtcNow(),
                bytesRead: 0, // batch processor doesn't expose byte counts; line count is the useful signal
                linesEmitted: results?.Count ?? 0);

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
