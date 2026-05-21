namespace Mithril.WorldSim;

/// <summary>
/// Non-generic frame base. Lets a composer or producer return heterogeneous
/// frames (different payload types) from a single call without resorting to
/// <c>Frame&lt;object&gt;</c>-with-boxing. Concrete <see cref="Frame{TPayload}"/>
/// implements this; consumers downcast / pattern-match on the concrete type
/// (typically inside <see cref="IWorldEventBus"/>) to route to typed
/// subscribers.
/// </summary>
public interface IFrame
{
    /// <summary>
    /// Event time the frame represents (principle 1 — NOT the wall-clock when
    /// the producer / composer emitted the frame).
    /// </summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Payload boxed to <see cref="object"/>. Concrete callers should usually
    /// pattern-match on <see cref="Frame{TPayload}"/> instead of reading this
    /// directly — that route keeps the typed payload unboxed at the consumer.
    /// </summary>
    object Payload { get; }

    /// <summary>
    /// Compile-time payload type carried by the underlying <see cref="Frame{TPayload}"/>.
    /// Used by the world's bus to route heterogeneous <see cref="IFrame"/>
    /// emissions to typed subscribers.
    /// </summary>
    Type PayloadType { get; }
}

/// <summary>
/// One unit of simulated input (principle 1 — "frame = (timestamp, payload)" — the
/// unifying primitive of the world-simulator architecture). Producers stamp every
/// frame with the event time the frame represents (NOT the wall-clock when the
/// producer fired), so a replay against a recorded source stream reconstructs the
/// same trajectory the live run produced.
/// </summary>
/// <typeparam name="TPayload">
/// The payload type carried by this frame. The world's merger routes a
/// <see cref="Frame{TPayload}"/> to its registered folder by the payload type.
/// </typeparam>
public readonly record struct Frame<TPayload>(
    DateTimeOffset Timestamp,
    TPayload Payload) : IFrame
{
    object IFrame.Payload => Payload!;
    Type IFrame.PayloadType => typeof(TPayload);
}
