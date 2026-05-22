using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace Mithril.WorldSim.Player.Internal;

/// <summary>
/// Typed pub-sub bus for a world's domain frames. Subscribers register a
/// handler for a payload type <c>T</c>; the bus routes
/// <see cref="Frame{TPayload}"/> emissions to handlers whose <c>T</c>
/// matches the frame's payload type.
///
/// <para>Handlers fire synchronously on the publishing thread (the world's
/// merger thread) in subscription order. Subscribers must not block; per
/// principle 11 — bus delivery happens inside the world's frame-resolution
/// loop and any blocking work stalls the merger.</para>
/// </summary>
internal sealed class WorldEventBus : IWorldEventBus
{
    private readonly ConcurrentDictionary<Type, List<Subscription>> _subscriptions = new();
    private readonly ConcurrentDictionary<Type, Func<DateTimeOffset, object, IFrame>> _changeEventWrappers = new();
    private readonly object _mutationLock = new();

    public IDisposable Subscribe<T>(Action<Frame<T>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(this, typeof(T), boxed =>
        {
            // The bus stores handlers as Action<IFrame> wrappers (post-cast to
            // the concrete generic Frame<T>) so a single dictionary covers all
            // payload types without reflection at publish-time.
            var typed = (Frame<T>)boxed;
            handler(typed);
        });

        var list = _subscriptions.GetOrAdd(typeof(T), _ => new List<Subscription>());
        lock (_mutationLock)
        {
            list.Add(subscription);
        }
        return subscription;
    }

    /// <summary>
    /// Remove a disposed subscription from its per-type list. Tolerant of
    /// double-dispose — calls past the first are a no-op because the entry is
    /// already gone. Hot path is publish, not dispose, so the List.Remove cost
    /// is acceptable.
    /// </summary>
    private void RemoveSubscription(Subscription subscription)
    {
        if (!_subscriptions.TryGetValue(subscription.PayloadType, out var list))
        {
            return;
        }
        lock (_mutationLock)
        {
            list.Remove(subscription);
        }
    }

    /// <summary>
    /// Test-only accessor for the subscriber-list count of a given payload
    /// type. Exposed via <c>InternalsVisibleTo</c> so the dispose-leak test
    /// can assert the list shrinks back to zero. Returns 0 when no subscriber
    /// has ever registered for <typeparamref name="T"/>.
    /// </summary>
    internal int SubscriberCountForTesting<T>()
    {
        if (!_subscriptions.TryGetValue(typeof(T), out var list))
        {
            return 0;
        }
        lock (_mutationLock)
        {
            return list.Count;
        }
    }

    /// <summary>
    /// Publish a frame to all matching subscribers. The frame's
    /// <see cref="IFrame.PayloadType"/> determines which subscriber list
    /// fires. Subscriptions added during a handler invocation do NOT
    /// observe the in-flight publish (snapshot is taken before dispatch).
    /// </summary>
    public void Publish(IFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!_subscriptions.TryGetValue(frame.PayloadType, out var list))
        {
            return;
        }

        // Snapshot the list under the mutation lock so handlers can subscribe
        // / unsubscribe without corrupting the iteration. The dispatch cost
        // is one List<>.ToArray() per publish; the bus is on the merger hot
        // path so we keep it allocation-light when there are no subscribers
        // by skipping the snapshot above.
        Subscription[] snapshot;
        lock (_mutationLock)
        {
            snapshot = list.ToArray();
        }

        foreach (var sub in snapshot)
        {
            if (sub.IsDisposed) continue;
            sub.Invoke(frame);
        }
    }

    /// <summary>
    /// Publish a folder-emitted change event as a typed
    /// <see cref="Frame{TPayload}"/> on the bus. The payload's runtime type
    /// determines <c>T</c>: <c>changeEvent.GetType() == typeof(TConcrete)</c>
    /// produces a <c>Frame&lt;TConcrete&gt;</c> stamped with
    /// <paramref name="timestamp"/>. Subscribers registered via
    /// <see cref="Subscribe{T}(Action{Frame{T}})"/> for the concrete change-event
    /// type see the emission; subscribers for a base type or
    /// <see cref="IChangeEvent"/> itself do not (the routing key is the
    /// concrete payload type, matching how composer emissions are routed).
    ///
    /// <para>This is the bridge that surfaces folder change events on the
    /// world's bus — without it, single-world consumers (per design notebook
    /// principle 4) would have to register a pass-through composer for every
    /// folder. The folder still returns <see cref="IChangeEvent"/> lists so
    /// the runtime-type lookup is one cached <c>Func</c> per concrete
    /// change-event type (first publish compiles, subsequent ones are direct
    /// delegate calls).</para>
    /// </summary>
    public void PublishChangeEvent(object changeEvent, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(changeEvent);

        var wrapper = _changeEventWrappers.GetOrAdd(changeEvent.GetType(), MakeWrapper);
        Publish(wrapper(timestamp, changeEvent));
    }

    /// <summary>
    /// Build a compiled delegate that, for a fixed runtime type <c>T</c>,
    /// projects <c>(timestamp, boxedPayload) =&gt; (IFrame) new Frame&lt;T&gt;(timestamp, (T)boxedPayload)</c>.
    /// One allocation per change-event type on first use; cached for the
    /// lifetime of the bus. Equivalent to a hand-rolled
    /// <c>MakeGenericMethod</c> + <c>Delegate.CreateDelegate</c> pair, but the
    /// expression tree is easier to read at the call site.
    /// </summary>
    private static Func<DateTimeOffset, object, IFrame> MakeWrapper(Type runtimeType)
    {
        var frameType = typeof(Frame<>).MakeGenericType(runtimeType);
        var ctor = frameType.GetConstructor(new[] { typeof(DateTimeOffset), runtimeType })
            ?? throw new InvalidOperationException(
                $"Frame<{runtimeType.FullName}> is missing the expected (DateTimeOffset, T) constructor.");
        var tsParam = Expression.Parameter(typeof(DateTimeOffset), "timestamp");
        var payloadParam = Expression.Parameter(typeof(object), "payload");
        var body = Expression.Convert(
            Expression.New(ctor, tsParam, Expression.Convert(payloadParam, runtimeType)),
            typeof(IFrame));
        return Expression.Lambda<Func<DateTimeOffset, object, IFrame>>(body, tsParam, payloadParam).Compile();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly WorldEventBus _owner;
        private readonly Action<IFrame> _handler;

        public Subscription(WorldEventBus owner, Type payloadType, Action<IFrame> handler)
        {
            _owner = owner;
            PayloadType = payloadType;
            _handler = handler;
        }

        public Type PayloadType { get; }

        public bool IsDisposed { get; private set; }

        public void Invoke(IFrame frame)
        {
            // Pattern-match on the concrete generic Frame<T> via the boxed
            // IFrame to keep the typed payload unboxed at the consumer
            // (Frame<T> is a readonly record struct — unboxing is free here
            // because the JIT sees the concrete cast in WorldEventBus.Subscribe).
            _handler(frame);
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            _owner.RemoveSubscription(this);
        }
    }
}
