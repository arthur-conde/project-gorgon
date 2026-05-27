using Arda.Abstractions.Diagnostics;
using Arda.Abstractions.Logs;
using Arda.Contracts.State.Health;
using Microsoft.Extensions.Logging;

namespace Arda.Dispatch;

/// <summary>
/// The L2 dispatch loop. Pulls lines from an <see cref="ILogLineSource"/>,
/// extracts verbs via <see cref="VerbExtractor"/>, and routes to the
/// <see cref="DispatchTable"/>. Optionally signals when the stream transitions
/// from replay to live (first line with <c>IsReplay = false</c>).
/// <para>
/// On a <see cref="GrammarException"/> escaping the dispatch table, the
/// driver records a <see cref="GrammarBreak"/> on the shared
/// <see cref="IGrammarBreakSignal"/> and returns — the companion driver
/// halts cooperatively at its next loop iteration. With
/// <paramref name="tolerantGrammar"/>=true, the exception is downgraded
/// to a throttled warning and the loop continues (dev escape hatch).
/// </para>
/// </summary>
internal sealed class WorldDriver : IWorldDriver
{
    private readonly ILogLineSource _source;
    private readonly DispatchTable _dispatch;
    private readonly Action? _onLiveTransition;
    private readonly IReadOnlyList<ILineObserver> _observers;
    private readonly ILogger? _logger;
    private readonly string? _sourceFamily;
    private readonly IGrammarBreakSignal? _grammarSignal;
    private readonly TimeProvider _time;
    private readonly bool _tolerantGrammar;
    private long _lineCount;

    public WorldDriver(
        ILogLineSource source,
        DispatchTable dispatch,
        Action? onLiveTransition = null,
        IReadOnlyList<ILineObserver>? observers = null,
        ILogger? logger = null,
        string? sourceFamily = null,
        IGrammarBreakSignal? grammarSignal = null,
        TimeProvider? time = null,
        bool tolerantGrammar = false)
    {
        _source = source;
        _dispatch = dispatch;
        _onLiveTransition = onLiveTransition;
        _observers = observers ?? [];
        _logger = logger;
        _sourceFamily = sourceFamily;
        _grammarSignal = grammarSignal;
        _time = time ?? TimeProvider.System;
        _tolerantGrammar = tolerantGrammar;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var liveSignalled = _onLiveTransition is null;
        var halted = false;

        // One long-running span per driver run. When no listener is attached
        // StartActivity returns null and the per-line cost stays ~zero.
        using var driverActivity = ArdaActivitySources.Dispatch.StartActivity("world_driver");
        driverActivity?.SetTag("source.family", _sourceFamily ?? "unknown");

        var sourceTag = new KeyValuePair<string, object?>("source", _sourceFamily ?? "unknown");

        await foreach (var line in _source.Lines(ct).WithCancellation(ct))
        {
            if (_grammarSignal?.IsRaised == true)
            {
                _logger?.LogInformation(
                    "Halting {SourceFamily} driver: companion driver raised grammar break",
                    _sourceFamily ?? "unknown");
                halted = true;
                break;
            }

            _lineCount++;
            ArdaMeters.LinesParsed.Add(1, sourceTag);

            if (!liveSignalled && !line.Metadata.IsReplay)
            {
                liveSignalled = true;
                _logger?.LogInformation(
                    "Replay to live transition for {SourceFamily}",
                    _sourceFamily ?? "unknown");
                _onLiveTransition!();
            }

            foreach (var observer in _observers)
                observer.Observe(line.Log, line.Metadata);

            var parsed = VerbExtractor.Parse(line.Log.AsSpan());
            if (parsed.IsEmpty)
            {
                ArdaMeters.VerbUnmatched.Add(1, sourceTag);
            }
            try
            {
                if (_tolerantGrammar)
                {
                    // Tolerant mode: per-handler recovery. A grammar fault in
                    // one handler must not silently skip its registered sibling
                    // handlers for the same verb — that's how tolerant mode
                    // would invisibly desync unrelated state.
                    _dispatch.Dispatch(parsed, line.Log, line.Metadata, OnTolerantBreak);
                }
                else
                {
                    _dispatch.Dispatch(parsed, line.Log, line.Metadata);
                }
            }
            catch (GrammarException ex)
            {
                // Strict mode: halt the whole pipeline at the first grammar drift.
                var details = new GrammarBreak(
                    _sourceFamily ?? "unknown",
                    ex.Verb,
                    ex.SourceLine,
                    ex.TokenExcerpt,
                    ex.ParserHint,
                    _time.GetUtcNow());

                _grammarSignal?.Raise(details);
                ArdaMeters.GrammarBreak.Add(1,
                    sourceTag,
                    new KeyValuePair<string, object?>("verb", ex.Verb));

                _logger?.LogError(ex,
                    "Grammar break halted {SourceFamily} driver (verb={Verb}, hint={Hint})",
                    _sourceFamily ?? "unknown", ex.Verb, ex.ParserHint);

                halted = true;
                break;
            }
        }

        bool OnTolerantBreak(GrammarException ex, IFrameHandler handler)
        {
            var details = new GrammarBreak(
                _sourceFamily ?? "unknown",
                ex.Verb,
                ex.SourceLine,
                ex.TokenExcerpt,
                ex.ParserHint,
                _time.GetUtcNow());
            _grammarSignal?.MarkObserved(details);
            ArdaMeters.GrammarBreak.Add(1,
                sourceTag,
                new KeyValuePair<string, object?>("verb", ex.Verb));
            _logger?.LogWarning(
                "Tolerant grammar mode: skipping handler {Handler} on {SourceFamily} (verb={Verb}, hint={Hint})",
                handler.GetType().Name, _sourceFamily ?? "unknown", ex.Verb, ex.ParserHint);
            return true;
        }

        // Only force a live transition if the source ran genuinely dry
        // (finite stream — typical of tests). On cancellation OR halt the
        // live signal would resolve replay-complete latches and trigger
        // flush-on-replay subscribers (e.g. PerCharacterStore) to write a
        // partial snapshot, so we must not fire it during shutdown / halt.
        if (!liveSignalled && !ct.IsCancellationRequested && !halted)
        {
            _logger?.LogWarning(
                "Live transition forced at end of stream for {SourceFamily} ({LineCount} lines processed)",
                _sourceFamily ?? "unknown",
                _lineCount);
            _onLiveTransition!();
        }

        driverActivity?.SetTag("line_count", _lineCount);
        driverActivity?.SetTag("halted", halted);

        _logger?.LogInformation(
            "World driver completed for {SourceFamily} ({LineCount} lines processed, halted={Halted})",
            _sourceFamily ?? "unknown",
            _lineCount,
            halted);
    }
}
