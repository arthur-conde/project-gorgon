using System.Globalization;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Stateful effect lifecycle handler. Maintains a catalog-id-keyed live set
/// with instance-to-catalog bridging (via <c>ProcessUpdateEffectName</c>'s
/// instance id). Handles its own verbs directly (adapter collapse):
/// AddEffects, RemoveEffects, UpdateEffectName.
/// </summary>
internal sealed class Effects : IEffectsState
{
    private readonly IDomainEventPublisher _bus;

    private readonly Dictionary<int, EffectStateEntry> _active = new();
    private readonly Dictionary<long, int> _instanceToCatalog = new();
    private readonly Stack<int> _unnamed = new();

    public IReadOnlyDictionary<int, EffectStateEntry> ActiveEffects => _active;

    public Effects(IDomainEventPublisher bus) => _bus = bus;

    internal IFrameHandler AddHandler => new AddVerb(this);
    internal IFrameHandler RemoveHandler => new RemoveVerb(this);
    internal IFrameHandler UpdateNameHandler => new UpdateNameVerb(this);

    public bool TryGet(int catalogId, out EffectStateEntry state)
        => _active.TryGetValue(catalogId, out state);

    internal void Reset()
    {
        _active.Clear();
        _instanceToCatalog.Clear();
        _unnamed.Clear();
    }

    /// <summary>
    /// <c>ProcessAddEffects(targetCharId, sourceCharId, "[catalogId1, catalogId2, ...]", bool)</c>
    /// </summary>
    private void OnAdd(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        tok.Skip(1); // targetCharId
        var sourceCharId = tok.NextLong();

        var listSpan = tok.NextQuotedSpan();
        if (listSpan.IsEmpty)
            return;

        var ids = ParseCatalogIds(listSpan);
        if (ids.Count == 0)
            return;

        var ts = metadata.Timestamp ?? metadata.ReadOn;

        foreach (var catalogId in ids)
        {
            if (_active.TryGetValue(catalogId, out var existing))
            {
                _active[catalogId] = existing with { AppliedAt = ts };
                continue;
            }

            var state = new EffectStateEntry(catalogId, null, null, sourceCharId, ts);
            _active[catalogId] = state;
            _unnamed.Push(catalogId);
        }

        _bus.Publish(new EffectsAdded(ids, sourceCharId, metadata));
    }

    /// <summary>
    /// <c>ProcessUpdateEffectName(targetCharId, instanceId, "displayName")</c>
    /// </summary>
    private void OnUpdateName(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        tok.Skip(1); // targetCharId
        var instanceId = tok.NextLong();
        var displayName = tok.NextQuotedSpan();
        if (displayName.IsEmpty)
            return;

        var name = displayName.ToString();

        if (_instanceToCatalog.TryGetValue(instanceId, out var knownCatalogId))
        {
            if (_active.TryGetValue(knownCatalogId, out var existing))
            {
                if (existing.DisplayName != name)
                    _active[knownCatalogId] = existing with { DisplayName = name };
            }
            else
            {
                _instanceToCatalog.Remove(instanceId);
            }
        }
        else
        {
            while (_unnamed.Count > 0)
            {
                var catalogId = _unnamed.Pop();
                if (!_active.TryGetValue(catalogId, out var state) || state.InstanceId is not null)
                    continue;

                _active[catalogId] = state with { InstanceId = instanceId, DisplayName = name };
                _instanceToCatalog[instanceId] = catalogId;
                break;
            }
        }

        _bus.Publish(new EffectNameUpdated(instanceId, name, metadata));
    }

    /// <summary>
    /// <c>ProcessRemoveEffects(targetCharId, [instanceId1, instanceId2, ...])</c>
    /// </summary>
    private void OnRemove(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
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

        foreach (var instanceId in ids)
        {
            int? matchedCatalogId = null;
            foreach (var (catalogId, state) in _active)
            {
                if (state.InstanceId == instanceId)
                {
                    matchedCatalogId = catalogId;
                    break;
                }
            }

            if (matchedCatalogId is not null)
            {
                _active.Remove(matchedCatalogId.Value);
                _instanceToCatalog.Remove(instanceId);
            }
        }

        _bus.Publish(new EffectsRemoved(ids, metadata));
    }

    private static List<int> ParseCatalogIds(ReadOnlySpan<char> span)
    {
        var result = new List<int>();
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

    private sealed class AddVerb(Effects owner) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
            => owner.OnAdd(args, sourceLog, metadata);
    }

    private sealed class RemoveVerb(Effects owner) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
            => owner.OnRemove(args, sourceLog, metadata);
    }

    private sealed class UpdateNameVerb(Effects owner) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
            => owner.OnUpdateName(args, sourceLog, metadata);
    }
}
