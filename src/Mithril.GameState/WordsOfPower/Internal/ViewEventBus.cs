using System.Collections.Concurrent;
using Mithril.WorldSim;

namespace Mithril.GameState.WordsOfPower.Internal;

/// <summary>
/// Typed pub-sub bus for the WoP view's emissions. Same shape as the
/// inventory view's bus (which see for design rationale) — handlers fire
/// synchronously on the publishing thread (the world's merger thread).
/// Subscribers must not block.
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
