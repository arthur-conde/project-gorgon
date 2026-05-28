using System.Collections.Concurrent;
using System.Diagnostics;
using Arda.Abstractions.Diagnostics;
using Arda.Dispatch.Internal;
using Microsoft.Extensions.Logging;

namespace Arda.Dispatch;

/// <summary>
/// Lock-free typed pub-sub implementation. Subscriptions are stored per-type
/// in a copy-on-write list for thread-safe enumeration during publish.
/// <para>
/// Publish is synchronous — subscribers execute inline on the driver thread.
/// A throwing subscriber is caught and logged; it does not prevent remaining
/// subscribers from receiving the event. <see cref="GrammarException"/> is the
/// one carve-out — it propagates so <c>WorldDriver</c> can halt the pipeline,
/// since a grammar drift means the in-memory world model is no longer
/// trustworthy. Subscribers later in the fanout don't see the event in that
/// case, which is intentional: there is no recovering from a grammar break.
/// </para>
/// <para>
/// Caveat on a grammar break thrown by a subscriber: the exception unwinds
/// through the publishing handler's frame, so <see cref="DispatchTable"/>
/// attributes it to that handler rather than to the subscriber. Strict-mode
/// halt is still correct (the world stops); tolerant mode will skip the
/// publishing handler for this line, which is the wrong target. A diagnostic
/// log warning captures the subscriber's identity before re-throw so the
/// attribution can be reconstructed from logs. See
/// <see href="https://github.com/moumantai-gg/mithril/issues/814">#814</see>
/// finding #4 for the follow-up to make this structurally correct.
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
        ArdaMeters.DomainEventPublished.Add(1,
            new KeyValuePair<string, object?>("event.type", typeof(T).Name));
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
            // HasListeners is a cheap volatile read; gates the string allocation
            // for the op name when no perf-recorder session has attached a listener.
            // Cached once per Publish call — a listener that attaches mid-publish misses
            // spans for this event's remaining subscribers. Acceptable trade-off: cheaper
            // than per-subscriber volatile reads, and the next Publish picks it up.
            var instrument = ArdaActivitySources.Composition.HasListeners();
            foreach (var handler in snapshot)
            {
                Activity? span = null;
                if (instrument)
                {
                    var target = handler.Target?.GetType().Name ?? "static";
                    span = ArdaActivitySources.Composition.StartActivity($"compose.{target}");
                    span?.SetTag("event", typeof(T).Name);
                }
                try
                {
                    handler(domainEvent);
                }
                catch (GrammarException ex)
                {
                    // Grammar break is the world-halt signal — must propagate
                    // out of the publish loop the same way DispatchTable lets
                    // it escape the handler loop. We can't tell DispatchTable
                    // that the throw came from a subscriber rather than from
                    // the publishing handler, so log the subscriber's identity
                    // here — the WorldDriver halt log will name the publishing
                    // handler, and operators reconcile via this warning.
                    logger.LogWarning(ex,
                        "Subscriber {Target}.{Method} threw {Exception} handling {EventType} — grammar break will propagate via the publishing handler's frame",
                        handler.Target?.GetType().Name ?? "(static)",
                        handler.Method.Name,
                        nameof(GrammarException),
                        typeof(T).Name);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Subscriber threw handling {EventType}", typeof(T).Name);
                }
                finally
                {
                    span?.Dispose();
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
