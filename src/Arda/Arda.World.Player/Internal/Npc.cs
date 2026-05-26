using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tracks NPC interaction context and correlates the gift verb triple
/// (<c>ProcessStartInteraction</c> / <c>ProcessDeleteItem</c> /
/// <c>ProcessDeltaFavor</c>) to emit <see cref="GiftAccepted"/>.
/// <para>
/// Registered for <c>ProcessStartInteraction</c> (primary),
/// <c>ProcessDeleteItem</c> (shared with <see cref="Inventory"/>), and
/// <c>ProcessDeltaFavor</c> (via <see cref="DeltaFavorHandler"/> adapter).
/// The game emits delete and favor-delta in either order; the pending FSM
/// handles both sequences.
/// </para>
/// </summary>
internal sealed class Npc : INpcState
{
    private readonly IDomainEventPublisher _bus;
    private readonly InternPool _npcPool;

    private (long EntityId, string NpcKey, long ItemInstanceId, LogLineMetadata Metadata)? _pendingDelete;
    private (string NpcKey, double Delta, LogLineMetadata Metadata)? _pendingDelta;

    public string? ActiveNpcKey { get; private set; }
    public long? ActiveEntityId { get; private set; }
    public double? ActiveFavor { get; private set; }

    public Npc(IDomainEventPublisher bus, InternPool npcPool)
    {
        _bus = bus;
        _npcPool = npcPool;
    }

    /// <summary>
    /// Clear interaction and pending gift state on area transition or
    /// character switch.
    /// </summary>
    internal void Reset()
    {
        ClearInteraction();
        _pendingDelete = null;
        _pendingDelta = null;
    }

    /// <summary>
    /// Args format: <c>(entityId, arg2, favor, isNpc, "Name")</c>
    /// Example: <c>(12307, 7, 2405.813, True, "NPC_Joe")</c>
    /// </summary>
    internal void OnStartInteraction(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        _pendingDelete = null;
        _pendingDelta = null;

        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        var entityId = tok.NextLong();
        tok.NextDouble(); // arg2 — semantics unclear, skip
        var favor = tok.NextDouble();
        var isNpc = tok.NextBool();
        var nameSpan = tok.NextQuotedSpan();

        var name = nameSpan.IsEmpty ? string.Empty : _npcPool.InternOrAllocate(nameSpan);

        if (isNpc && name.Length > 0)
        {
            ActiveNpcKey = name;
            ActiveEntityId = entityId;
            ActiveFavor = favor;
        }
        else
        {
            ClearInteraction();
        }

        _bus.Publish(new InteractionStarted(entityId, name, favor, isNpc, metadata));
    }

    /// <summary>
    /// Correlate item deletion during active NPC interaction as a gift attempt.
    /// If a pending favor delta already arrived, emit <see cref="GiftAccepted"/>
    /// immediately; otherwise stash the delete as pending.
    /// Args format: <c>(instanceId)</c>
    /// </summary>
    internal void OnDeleteItem(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        if (ActiveNpcKey is null || ActiveEntityId is null)
            return;

        var inner = SpanHelpers.StripParens(args);
        if (inner.IsEmpty)
            return;

        if (!long.TryParse(inner, out var instanceId))
            return;

        if (_pendingDelta is { } delta && delta.NpcKey == ActiveNpcKey)
        {
            _pendingDelta = null;
            _bus.Publish(new GiftAccepted(
                ActiveEntityId.Value, ActiveNpcKey, instanceId, delta.Delta, delta.Metadata));
            return;
        }

        _pendingDelete = (ActiveEntityId.Value, ActiveNpcKey, instanceId, metadata);
    }

    /// <summary>
    /// Correlate a positive favor delta during active NPC interaction. If a
    /// pending item deletion already arrived, emit <see cref="GiftAccepted"/>
    /// immediately; otherwise stash the delta as pending.
    /// Args format: <c>(entityId, "NPC_Key", delta, bool)</c>
    /// </summary>
    internal void OnDeltaFavor(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        tok.NextLong(); // entityId — already tracked
        var npcKey = tok.NextQuotedSpan();
        var delta = tok.NextDouble();

        if (delta <= 0)
            return;

        if (ActiveNpcKey is null)
            return;

        if (!npcKey.SequenceEqual(ActiveNpcKey))
            return;

        if (_pendingDelete is { } pending && pending.NpcKey == ActiveNpcKey)
        {
            _pendingDelete = null;
            _bus.Publish(new GiftAccepted(
                pending.EntityId, pending.NpcKey, pending.ItemInstanceId, delta, metadata));
            return;
        }

        _pendingDelta = (ActiveNpcKey, delta, metadata);
    }

    private void ClearInteraction()
    {
        ActiveNpcKey = null;
        ActiveEntityId = null;
        ActiveFavor = null;
    }
}
