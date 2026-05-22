using System.Collections.Concurrent;
using Mithril.WorldSim;

namespace Mithril.GameState.Inventory;

/// <summary>
/// Typed pub-sub bus for the view's three view-emitted change-event channels
/// (<see cref="InventoryItemAdded"/>, <see cref="InventoryItemRemoved"/>,
/// <see cref="InventoryStackChanged"/>) — same shape as
/// <see cref="IWorldEventBus"/> but lives on the view rather than a world.
/// Subscribers register a handler for a payload type <c>T</c>; the bus routes
/// <see cref="Frame{T}"/> emissions to handlers whose <c>T</c> matches the
/// frame's payload type.
///
/// <para>The world buses include a compiled-delegate path
/// (<c>PublishChangeEvent</c>) for runtime-typed change-event publishing from
/// folder-return values — the view doesn't need that hot path because all
/// three view-emitted types are known at compile time and we publish each via
/// a typed <see cref="Frame{T}"/> directly. The simpler <see cref="Publish"/>
/// is the only required surface.</para>
/// </summary>
internal sealed class ViewEventBus : IWorldEventBus
{
    private readonly ConcurrentDictionary<Type, List<Subscription>> _subscriptions = new();
    private readonly object _mutationLock = new();

    public IDisposable Subscribe<T>(Action<Frame<T>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(typeof(T), boxed =>
        {
            var typed = (Frame<T>)boxed;
            handler(typed);
        });

        var list = _subscriptions.GetOrAdd(typeof(T), _ => new List<Subscription>());
        lock (_mutationLock) { list.Add(subscription); }
        return subscription;
    }

    public void Publish(IFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_subscriptions.TryGetValue(frame.PayloadType, out var list)) return;

        Subscription[] snapshot;
        lock (_mutationLock) { snapshot = list.ToArray(); }

        foreach (var sub in snapshot)
        {
            if (sub.IsDisposed) continue;
            sub.Invoke(frame);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Type _payloadType;
        private readonly Action<IFrame> _handler;

        public Subscription(Type payloadType, Action<IFrame> handler)
        {
            _payloadType = payloadType;
            _handler = handler;
        }

        public bool IsDisposed { get; private set; }

        public void Invoke(IFrame frame) => _handler(frame);
        public void Dispose() => IsDisposed = true;
    }
}
