using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tracks NPC interaction context. Emits <see cref="InteractionStarted"/> for all
/// interaction targets (NPCs, chests, plants) and <see cref="GiftAttempted"/> when
/// an item deletion occurs during an active NPC interaction.
/// <para>
/// Registered for <c>ProcessStartInteraction</c> (as primary handler) and
/// <c>ProcessDeleteItem</c> (shared with <see cref="Inventory"/>) for gift correlation.
/// </para>
/// </summary>
internal sealed class Npc : INpcState
{
    private readonly IDomainEventPublisher _bus;
    private readonly InternPool _npcPool;

    public string? ActiveNpcKey { get; private set; }
    public long? ActiveEntityId { get; private set; }
    public double? ActiveFavor { get; private set; }

    public Npc(IDomainEventPublisher bus, InternPool npcPool)
    {
        _bus = bus;
        _npcPool = npcPool;
    }

    /// <summary>
    /// Clear interaction context on area transition or character switch.
    /// Prevents stale <see cref="GiftAttempted"/> emissions in the new zone.
    /// </summary>
    internal void Reset() => ClearInteraction();

    /// <summary>
    /// Args format: <c>(entityId, arg2, favor, isNpc, "Name")</c>
    /// Example: <c>(12307, 7, 2405.813, True, "NPC_Joe")</c>
    /// </summary>
    internal void OnStartInteraction(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
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

        _bus.Publish(new GiftAttempted(ActiveEntityId.Value, ActiveNpcKey, instanceId, metadata));
    }

    private void ClearInteraction()
    {
        ActiveNpcKey = null;
        ActiveEntityId = null;
        ActiveFavor = null;
    }
}
