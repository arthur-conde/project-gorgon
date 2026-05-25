using System.Collections.Frozen;
using Arda.Abstractions.Logs;

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

    public DispatchTable(Dictionary<string, List<IFrameHandler>> registry)
    {
        _handlers = registry.ToFrozenDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToArray());
        _lookup = _handlers.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>
    /// Dispatch a line to registered handlers. If no handler is registered for the
    /// verb, the line is silently discarded (zero allocation).
    /// </summary>
    public void Dispatch(ReadOnlySpan<char> verbSpan, ReadOnlySpan<char> logSpan, string sourceLog, LogLineMetadata metadata)
    {
        if (verbSpan.IsEmpty)
            return;

        if (!_lookup.TryGetValue(verbSpan, out var handlers))
            return;

        var args = VerbExtractor.ExtractArgs(logSpan);
        foreach (var handler in handlers)
            handler.Handle(args, sourceLog, metadata);
    }
}
