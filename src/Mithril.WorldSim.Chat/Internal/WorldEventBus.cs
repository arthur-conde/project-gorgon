using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace Mithril.WorldSim.Chat.Internal;

/// <summary>
/// Typed pub-sub bus for the chat world's domain frames. Same shape as the
/// Player-side world bus — handlers fire synchronously on the publishing
/// thread (the world's merger thread) in subscription order. Subscribers must
/// not block; per principle 11 — bus delivery happens inside the world's
/// frame-resolution loop and any blocking work stalls the merger.
/// </summary>
internal sealed class WorldEventBus : IWorldEventBus
{
    private readonly ConcurrentDictionary<Type, List<Subscription>> _subscriptions = new();
    private readonly ConcurrentDictionary<Type, Func<DateTimeOffset, object, IFrame>> _changeEventWrappers = new();
    private readonly object _mutationLock = new();

    public IDisposable Subscribe<T>(Action<Frame<T>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(typeof(T), boxed =>
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
    /// Publish a frame to all matching subscribers. The frame's
    /// <see cref="IFrame.PayloadType"/> determines which subscriber list
    /// fires. Subscriptions added during a handler invocation do NOT observe
    /// the in-flight publish (snapshot is taken before dispatch).
    /// </summary>
    public void Publish(IFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!_subscriptions.TryGetValue(frame.PayloadType, out var list))
        {
            return;
        }

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
    /// Surface a folder change event on the bus as a typed
    /// <see cref="Frame{TPayload}"/>. Mirrors the PlayerWorld bus implementation;
    /// see that copy for the design rationale (principle 4 — single-world
    /// consumers subscribe directly to the world's bus).
    /// </summary>
    public void PublishChangeEvent(object changeEvent, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(changeEvent);

        var wrapper = _changeEventWrappers.GetOrAdd(changeEvent.GetType(), MakeWrapper);
        Publish(wrapper(timestamp, changeEvent));
    }

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
