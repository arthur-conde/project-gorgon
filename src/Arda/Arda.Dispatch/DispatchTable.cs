using System.Collections.Frozen;
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
    /// prevent subsequent handlers from executing on the same line. A
    /// <see cref="GrammarException"/> is NOT caught here: it propagates out so
    /// the surrounding <c>WorldDriver</c> can halt the simulation, since a
    /// grammar drift means the in-memory world model is no longer trustworthy.
    /// </summary>
    public void Dispatch(ParsedVerb parsed, string sourceLog, LogLineMetadata metadata)
    {
        if (parsed.IsEmpty)
            return;

        if (!_lookup.TryGetValue(parsed.Verb, out var handlers))
            return;

        foreach (var handler in handlers)
        {
            try
            {
                handler.Handle(parsed.Args, parsed.Verb, sourceLog, metadata);
            }
            catch (GrammarException)
            {
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

    private static string Excerpt(string log) =>
        log.Length <= 200 ? log : log[..200] + "…";
}
