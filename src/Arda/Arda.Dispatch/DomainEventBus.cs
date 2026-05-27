using System.Collections.Concurrent;
using Arda.Contracts;
using Microsoft.Extensions.Logging;

namespace Arda.Dispatch;

/// <summary>
/// Lock-free typed pub-sub implementation. Subscriptions are stored per-type
/// in a copy-on-write list for thread-safe enumeration during publish.
/// <para>
/// Publish is synchronous — subscribers execute inline on the driver thread.
/// A throwing subscriber is caught and logged; it does not prevent remaining
/// subscribers from receiving the event.
/// </para>
/// </summary>
internal sealed class DomainEventBus : IDomainEventBus
{
    private readonly ConcurrentDictionary<Type, object> _subscriptions = new();
    private readonly ILogger<DomainEventBus> _logger;

    public DomainEventBus(ILogger<DomainEventBus> logger)
    {
        _logger = logger;
    }

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
            ((SubscriptionList<T>)obj).Publish(domainEvent, _logger);
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

        public void Publish(T domainEvent, ILogger logger)
        {
            var snapshot = _handlers;
            foreach (var handler in snapshot)
            {
                try
                {
                    handler(domainEvent);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Subscriber threw handling {EventType}", typeof(T).Name);
                }
            }
        }

        internal sealed class Subscription(SubscriptionList<T> list, Action<T> handler) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    list.Remove(handler);
            }
        }
    }
}
