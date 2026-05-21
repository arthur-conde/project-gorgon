namespace Mithril.WorldSim;

/// <summary>
/// A source of external-input frames feeding a world (principle 10). Implementers
/// are sources of real-world inputs only: the L1 classified-pipe reader (Player.log
/// frames), the chat tail (chat frames). Future possibilities: filesystem reconcile
/// emitting frames stamped with export payload timestamps. Producers are NOT a
/// mechanism for user-driven scheduling — user-side wake-at-T concerns consume
/// world domain events and run module-internal logic against them; they do not
/// register producers in a world's merger. The world is sealed at its input.
/// </summary>
/// <typeparam name="TPayload">
/// The payload type of frames this producer emits. Pairs with the folder
/// registered for the same payload type.
/// </typeparam>
public interface IFrameProducer<TPayload>
{
    /// <summary>
    /// Emits frames in ascending timestamp order. The world's merger is a
    /// priority queue keyed by <see cref="Frame{TPayload}.Timestamp"/>;
    /// producers must not emit out-of-order frames. Late-stamped frames
    /// (timestamp earlier than the world's clock) are clamped + warned by the
    /// world.
    /// </summary>
    IAsyncEnumerable<Frame<TPayload>> SubscribeAsync(CancellationToken ct);

    /// <summary>
    /// Used by the world's merger to break ties when two producers emit frames
    /// with identical timestamps. Lower priority dispatches first. Producer
    /// priorities must be declared at registration time; the world's tie-
    /// breaking is replay-deterministic over the producer set.
    /// </summary>
    int Priority { get; }
}
