using Arda.Abstractions.Logs;
using Arda.Ingest.Classification;
using Arda.Ingest.Clock;
using Arda.Ingest.Internal;
using Arda.Ingest.Tailer;

namespace Arda.Ingest.Coordinator;

/// <summary>
/// Shared batch-processing logic used by both <see cref="PlayerLogSource"/>
/// and <see cref="ChatLogSource"/>. Owns a reusable <see cref="List{T}"/>
/// that is cleared between batches to reduce GC pressure at high poll rates.
/// </summary>
internal sealed class BatchProcessor
{
    private readonly LineClassifier _classifier;
    private readonly TimeProvider _time;
    private readonly List<LogLine> _results = new(64);

    public BatchProcessor(LineClassifier classifier, TimeProvider time)
    {
        _classifier = classifier;
        _time = time;
    }

    /// <summary>
    /// Reads the next batch from <paramref name="tailer"/>, runs classification
    /// on each line, and returns the promoted lines. Returns <c>null</c> when
    /// no data is available.
    /// <para>
    /// Optionally calls <see cref="ILogSourceClock.EnsureAnchored"/> on the
    /// first batch when <paramref name="anchored"/> is <c>false</c>.
    /// </para>
    /// <para>
    /// The returned list is owned by this instance and is reused across calls —
    /// callers must consume it before the next <see cref="ProcessBatch"/> call.
    /// </para>
    /// </summary>
    public List<LogLine>? ProcessBatch(
        LogSourceTailer tailer,
        bool isReplay,
        ILogSourceClock? clock,
        ref bool anchored)
    {
        var batch = tailer.ReadNew();
        if (batch.IsEmpty) return null;

        try
        {
            // Pre-scan: a session banner anywhere in the batch can override
            // the date/timezone state (e.g. Player.log "Logged in as ...
            // Time UTC=YYYY-MM-DD HH:MM:SS"). Must run BEFORE EnsureAnchored
            // so banner wins over mtime fallback, and BEFORE classification
            // so the FIRST classified line is stamped against the banner.
            for (var i = 0; i < batch.LineCount; i++)
            {
                var (start, length) = batch.Lines[i];
                _classifier.Clock.TryConsumeBanner(batch.Buffer.AsSpan(start, length));
            }

            if (clock is not null && !anchored)
            {
                var lineSpans = batch.Lines.AsSpan(0, batch.LineCount);
                clock.EnsureAnchored(
                    lineSpans,
                    batch.Buffer,
                    () => File.GetLastWriteTimeUtc(tailer.Path));
                anchored = true;
            }

            var readOn = _time.GetUtcNow();
            _results.Clear();

            for (var i = 0; i < batch.LineCount; i++)
            {
                var (start, length) = batch.Lines[i];
                var lineSpan = batch.Buffer.AsSpan(start, length);

                var classified = _classifier.Classify(lineSpan);
                if (classified is null) continue;

                _results.Add(new LogLine(
                    classified.Value.Log,
                    new LogLineMetadata(
                        classified.Value.Timestamp,
                        readOn,
                        isReplay),
                    classified.Value.Raw));
            }

            return _results;
        }
        finally
        {
            batch.Dispose();
        }
    }

    /// <summary>
    /// Overload without anchoring (for sources like Chat where the clock
    /// doesn't need anchoring).
    /// </summary>
    public List<LogLine>? ProcessBatch(LogSourceTailer tailer, bool isReplay)
    {
        var ignored = true;
        return ProcessBatch(tailer, isReplay, clock: null, ref ignored);
    }
}
