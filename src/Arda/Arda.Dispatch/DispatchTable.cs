using System.Collections.Frozen;
using System.Diagnostics;
using Arda.Abstractions.Diagnostics;
using Arda.Abstractions.Logs;
using Microsoft.Extensions.Logging;

namespace Arda.Dispatch;

/// <summary>
/// Frozen handler registry keyed by verb. Uses
/// <see cref="FrozenDictionary{TKey,TValue}.GetAlternateLookup{TAlternateKey}"/>
/// for zero-allocation span-based lookup at dispatch time.
/// </summary>
internal sealed class DispatchTable
{
    private readonly FrozenDictionary<string, IFrameHandler[]> _handlers;
    private readonly FrozenDictionary<string, IFrameHandler[]>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly ILogger<DispatchTable> _logger;

    public DispatchTable(Dictionary<string, List<IFrameHandler>> registry, ILogger<DispatchTable> logger)
    {
        _handlers = registry.ToFrozenDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToArray());
        _lookup = _handlers.GetAlternateLookup<ReadOnlySpan<char>>();
        _logger = logger;
    }

    /// <summary>
    /// Parse and dispatch a line to registered handlers. If the line has no
    /// recognizable verb, or no handler is registered, it is silently discarded
    /// (zero allocation). A handler bug is caught and logged — it does not
    /// prevent subsequent handlers from executing on the same line.
    /// <para>
    /// A <see cref="GrammarException"/> from a handler is fatal by default — it
    /// propagates out so the surrounding <c>WorldDriver</c> can halt the
    /// simulation, since a grammar drift means the in-memory world model is no
    /// longer trustworthy. Callers running in tolerant mode supply
    /// <paramref name="onGrammarBreak"/>; if it returns <c>true</c> the
    /// offending handler is skipped but sibling handlers registered for the
    /// same verb still receive the line. This keeps a per-handler grammar
    /// fault from silently knocking out unrelated sibling state.
    /// </para>
    /// </summary>
    public void Dispatch(
        ParsedVerb parsed,
        string sourceLog,
        LogLineMetadata metadata,
        Func<GrammarException, IFrameHandler, bool>? onGrammarBreak = null)
    {
        if (parsed.IsEmpty)
            return;

        if (!_lookup.TryGetValue(parsed.Verb, out var handlers))
        {
            // Counter only — emitting a span for every uncovered line would explode the
            // span volume on noisy log sources. Tags keep this aggregable per (verb, src).
            // Gate on Enabled so the `parsed.Verb.ToString()` allocation only happens when
            // a listener is actually consuming the counter — preserves the "zero-cost when
            // no listener" claim on a high-frequency hot path.
            if (ArdaMeters.VerbUnhandled.Enabled)
            {
                ArdaMeters.VerbUnhandled.Add(1,
                    new KeyValuePair<string, object?>("verb", parsed.Verb.ToString()));
            }
            return;
        }

        // Per-verb span. Gated on Activity.Current so we only pay span allocation when
        // a parent (e.g. WorldDriver.world_driver) is open — that's the signal that
        // a listener is actually attached and wants per-line timing.
        Activity? activity = null;
        if (Activity.Current is not null)
        {
            activity = ArdaActivitySources.Dispatch.StartActivity("dispatch_verb");
            activity?.SetTag("verb", parsed.Verb.ToString());
            activity?.SetTag("handler.count", (long)handlers.Length);
        }

        try
        {

        foreach (var handler in handlers)
        {
            try
            {
                handler.Handle(parsed.Args, parsed.Verb, sourceLog, metadata);
            }
            catch (GrammarException ex)
            {
                if (onGrammarBreak is not null && onGrammarBreak(ex, handler))
                    continue;
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Handler {Handler} threw on verb {Verb}: {Excerpt}",
                    handler.GetType().Name, parsed.Verb.ToString(), Excerpt(sourceLog));
            }
        }

        }
        finally
        {
            activity?.Dispose();
        }
    }

    private static string Excerpt(string log) =>
        log.Length <= 200 ? log : log[..200] + "…";
}
