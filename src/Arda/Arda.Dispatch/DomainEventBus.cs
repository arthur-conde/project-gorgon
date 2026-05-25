using System.Collections.Concurrent;

namespace Arda.Dispatch;

/// <summary>
/// Lock-free typed pub-sub implementation. Subscriptions are stored per-type
/// in a copy-on-write list for thread-safe enumeration during publish.
/// <para>
/// Publish is synchronous — subscribers execute inline on the driver thread.
/// This is intentional: it keeps dispatch deterministic and avoids cross-thread
/// coordination for state machine updates.
/// </para>
/// </summary>
internal sealed class DomainEventBus : IDomainEventBus
{
    private readonly ConcurrentDictionary<Type, object> _subscriptions = new();

    public IDisposable Subscribe<T>(Action<T> handler) where T : struct
    {
        var list = (SubscriptionList<T>)_subscriptions.GetOrAdd(
            typeof(T),
            static _ => new SubscriptionList<T>());
        return list.Add(handler);
    }

    public void Publish<T>(T domainEvent) where T : struct
    {
        if (_subscriptions.TryGetValue(typeof(T), out var obj))
            ((SubscriptionList<T>)obj).Publish(domainEvent);
    }

    private sealed class SubscriptionList<T> where T : struct
    {
        private volatile Action<T>[] _handlers = [];

        public Subscription Add(Action<T> handler)
        {
            lock (this)
            {
                var current = _handlers;
                var next = new Action<T>[current.Length + 1];
                current.CopyTo(next, 0);
                next[^1] = handler;
                _handlers = next;
            }
            return new Subscription(this, handler);
        }

        public void Remove(Action<T> handler)
        {
            lock (this)
            {
                var current = _handlers;
                var idx = Array.IndexOf(current, handler);
                if (idx < 0) return;

                if (current.Length == 1)
                {
                    _handlers = [];
                    return;
                }

                var next = new Action<T>[current.Length - 1];
                current[..idx].CopyTo(next, 0);
                current[(idx + 1)..].CopyTo(next.AsSpan(idx));
                _handlers = next;
            }
        }

        public void Publish(T domainEvent)
        {
            var snapshot = _handlers;
            foreach (var handler in snapshot)
                handler(domainEvent);
        }

        internal sealed class Subscription(SubscriptionList<T> list, Action<T> handler) : IDisposable
        {
            public void Dispose() => list.Remove(handler);
        }
    }
}
