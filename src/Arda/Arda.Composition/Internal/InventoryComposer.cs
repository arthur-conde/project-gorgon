using Arda.Composition.Events;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;

namespace Arda.Composition.Internal;

/// <summary>
/// Correlates <see cref="InventoryItemAdded"/> (Player.log) with
/// <see cref="ChatInventoryObserved"/> (ChatLog) by <c>ReadOn</c> temporal
/// proximity. Emits <see cref="InventoryItemResolved"/> when both sides match.
/// Unmatched events are expired after a configurable window.
/// </summary>
internal sealed class InventoryComposer : IDisposable
{
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromSeconds(2);
    private const int MaxPending = 64;

    private readonly IDomainEventBus _bus;
    private readonly LinkedList<InventoryItemAdded> _pendingAdds = new();
    private readonly LinkedList<ChatInventoryObserved> _pendingChats = new();
    private IDisposable? _addSub;
    private IDisposable? _chatSub;

    public InventoryComposer(IDomainEventBus bus)
    {
        _bus = bus;
        _addSub = bus.Subscribe<InventoryItemAdded>(OnItemAdded);
        _chatSub = bus.Subscribe<ChatInventoryObserved>(OnChatObserved);
    }

    private void OnItemAdded(InventoryItemAdded added)
    {
        var node = _pendingChats.First;
        while (node is not null)
        {
            var chat = node.Value;
            var delta = added.Metadata.ReadOn - chat.Metadata.ReadOn;
            if (delta.Duration() <= CorrelationWindow)
            {
                _pendingChats.Remove(node);
                _bus.Publish(new InventoryItemResolved(
                    added.InstanceId,
                    added.InternalName,
                    chat.DisplayName,
                    chat.Count,
                    added.Metadata));
                return;
            }

            node = node.Next;
        }

        _pendingAdds.AddLast(added);
        TrimPending(_pendingAdds, added.Metadata.ReadOn);
    }

    private void OnChatObserved(ChatInventoryObserved chat)
    {
        var node = _pendingAdds.First;
        while (node is not null)
        {
            var add = node.Value;
            var delta = chat.Metadata.ReadOn - add.Metadata.ReadOn;
            if (delta.Duration() <= CorrelationWindow)
            {
                _pendingAdds.Remove(node);
                _bus.Publish(new InventoryItemResolved(
                    add.InstanceId,
                    add.InternalName,
                    chat.DisplayName,
                    chat.Count,
                    add.Metadata));
                return;
            }

            node = node.Next;
        }

        _pendingChats.AddLast(chat);
        TrimPending(_pendingChats, chat.Metadata.ReadOn);
    }

    private void TrimPending<T>(LinkedList<T> list, DateTimeOffset currentReadOn) where T : struct
    {
        while (list.Count > MaxPending)
            list.RemoveFirst();

        while (list.First is { } first)
        {
            var readOn = first.Value switch
            {
                InventoryItemAdded a => a.Metadata.ReadOn,
                ChatInventoryObserved c => c.Metadata.ReadOn,
                _ => currentReadOn
            };
            if (currentReadOn - readOn > CorrelationWindow)
                list.RemoveFirst();
            else
                break;
        }
    }

    public void Dispose()
    {
        _addSub?.Dispose();
        _chatSub?.Dispose();
        _addSub = null;
        _chatSub = null;
    }
}
