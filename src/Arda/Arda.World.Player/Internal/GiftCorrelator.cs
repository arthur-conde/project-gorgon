using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Correlates <see cref="GiftAttempted"/> and <see cref="DeltaFavorReceived"/>
/// to produce fully-resolved <see cref="GiftAccepted"/> events. Maintains its
/// own instanceId-to-InternalName map (from <see cref="InventoryItemAdded"/>)
/// to resolve item names without depending on <see cref="Inventory"/>'s mutable state.
/// </summary>
internal sealed class GiftCorrelator : IDisposable
{
    private readonly IDomainEventBus _bus;
    private readonly Dictionary<long, string> _instanceMap = new();

    private (long EntityId, string NpcKey, long ItemInstanceId, string ItemInternalName, LogLineMetadata Metadata)? _pendingDelete;
    private (string NpcKey, double Delta, LogLineMetadata Metadata)? _pendingDelta;

    private IDisposable? _addSub;
    private IDisposable? _giftSub;
    private IDisposable? _deltaSub;
    private IDisposable? _interactionSub;

    public GiftCorrelator(IDomainEventBus bus)
    {
        _bus = bus;
        _addSub = bus.Subscribe<InventoryItemAdded>(OnItemAdded);
        _giftSub = bus.Subscribe<GiftAttempted>(OnGiftAttempted);
        _deltaSub = bus.Subscribe<DeltaFavorReceived>(OnDeltaFavor);
        _interactionSub = bus.Subscribe<InteractionStarted>(OnInteractionStarted);
    }

    private void OnItemAdded(InventoryItemAdded added)
    {
        _instanceMap[added.InstanceId] = added.InternalName;
    }

    private void OnGiftAttempted(GiftAttempted gift)
    {
        if (!_instanceMap.TryGetValue(gift.ItemInstanceId, out var internalName))
            return;

        if (_pendingDelta is { } pending && pending.NpcKey == gift.NpcKey)
        {
            _pendingDelta = null;
            _bus.Publish(new GiftAccepted(
                gift.EntityId, gift.NpcKey, gift.ItemInstanceId,
                internalName, pending.Delta, pending.Metadata));
            return;
        }

        _pendingDelete = (gift.EntityId, gift.NpcKey, gift.ItemInstanceId, internalName, gift.Metadata);
    }

    private void OnDeltaFavor(DeltaFavorReceived delta)
    {
        if (_pendingDelete is { } pending && pending.NpcKey == delta.NpcKey)
        {
            _pendingDelete = null;
            _bus.Publish(new GiftAccepted(
                pending.EntityId, pending.NpcKey, pending.ItemInstanceId,
                pending.ItemInternalName, delta.Delta, delta.Metadata));
            return;
        }

        _pendingDelta = (delta.NpcKey, delta.Delta, delta.Metadata);
    }

    private void OnInteractionStarted(InteractionStarted started)
    {
        _pendingDelete = null;
        _pendingDelta = null;
    }

    public void Dispose()
    {
        _addSub?.Dispose();
        _giftSub?.Dispose();
        _deltaSub?.Dispose();
        _interactionSub?.Dispose();
        _addSub = null;
        _giftSub = null;
        _deltaSub = null;
        _interactionSub = null;
    }
}
