using System.Globalization;
using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Simplified effect lifecycle handler. Emits <see cref="EffectsAdded"/> and
/// <see cref="EffectsRemoved"/> from the raw <c>ProcessAddEffects</c> /
/// <c>ProcessRemoveEffects</c> verbs. Does not track the full add→name→remove
/// correlation (that complex state machine lives in the legacy
/// <c>PlayerEffectsStateService</c>); this handler captures the raw IDs for
/// downstream consumers that need the signal without the full state.
/// </summary>
internal sealed class Effects
{
    private readonly IDomainEventPublisher _bus;

    public Effects(IDomainEventPublisher bus) => _bus = bus;

    /// <summary>
    /// <c>ProcessAddEffects(targetCharId, sourceCharId, "[catalogId1, catalogId2, ...]", bool)</c>
    /// The bracketed list is inside a quoted string.
    /// </summary>
    internal void OnAdd(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        tok.Skip(2); // targetCharId, sourceCharId

        // Third arg is a quoted string containing "[id1, id2, ...]"
        var listSpan = tok.NextQuotedSpan();
        if (listSpan.IsEmpty)
            return;

        var ids = ParseCatalogIds(listSpan);
        if (ids.Count == 0)
            return;

        _bus.Publish(new EffectsAdded(ids, metadata));
    }

    /// <summary>
    /// <c>ProcessRemoveEffects(targetCharId, [instanceId1, instanceId2, ...])</c>
    /// The bracketed list is unquoted.
    /// </summary>
    internal void OnRemove(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        tok.Skip(1); // targetCharId

        var listSpan = tok.NextBracketedSpan();
        if (listSpan.IsEmpty)
            return;

        var ids = ParseInstanceIds(listSpan);
        if (ids.Count == 0)
            return;

        _bus.Publish(new EffectsRemoved(ids, metadata));
    }

    private static List<int> ParseCatalogIds(ReadOnlySpan<char> span)
    {
        var result = new List<int>();

        // Strip surrounding brackets if present
        var s = span;
        if (s.Length > 0 && s[0] == '[') s = s[1..];
        if (s.Length > 0 && s[^1] == ']') s = s[..^1];

        foreach (var range in s.Split(','))
        {
            var token = s[range].Trim();
            if (token.IsEmpty) continue;
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                result.Add(id);
        }

        return result;
    }

    private static List<long> ParseInstanceIds(ReadOnlySpan<char> span)
    {
        var result = new List<long>();
        foreach (var range in span.Split(','))
        {
            var token = span[range].Trim();
            if (token.IsEmpty) continue;
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                result.Add(id);
        }
        return result;
    }
}
