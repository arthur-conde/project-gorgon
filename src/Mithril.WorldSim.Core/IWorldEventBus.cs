namespace Mithril.WorldSim;

/// <summary>
/// Typed pub-sub for a world's domain frames. Subscribers see emissions in
/// resolution order (deterministic over the source stream — principle 2 +
/// principle 11). One bus per world; cross-world consumers (views) subscribe
/// to both world buses (principle 4 — "cross-source composition lives in a
/// view layer above the worlds").
/// </summary>
public interface IWorldEventBus
{
    /// <summary>
    /// Subscribe to domain frames of payload type <typeparamref name="T"/>.
    /// Returns an <see cref="IDisposable"/> handle that, when disposed, removes
    /// the subscription. Handlers are invoked synchronously during the world's
    /// frame resolution loop on the world's dispatch thread — subscribers must
    /// not block.
    /// </summary>
    IDisposable Subscribe<T>(Action<Frame<T>> handler);
}
