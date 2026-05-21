namespace Mithril.WorldSim;

/// <summary>
/// Shared contract for both worlds (PlayerWorld, ChatWorld — principle 2 —
/// "two worlds with sealed output boundaries; views consume across them").
/// Each world owns its own producers, folders, composers, clock, frame merger,
/// and output bus. Worlds don't query each other and don't send messages to
/// each other — they're sealed at the bus. Cross-world consumption goes
/// through a view layer one level up (principle 4).
/// </summary>
public interface IWorld
{
    /// <summary>
    /// This world's simulated wall-clock. Advances by the timestamp of the most
    /// recently applied frame; see <see cref="IWorldClock"/>.
    /// </summary>
    IWorldClock Clock { get; }

    /// <summary>
    /// Output bus for this world's domain frames. View-layer composers subscribe
    /// here; consumers outside the world never see change events directly — only
    /// the domain frames the world's composers chose to emit.
    /// </summary>
    IWorldEventBus Bus { get; }

    /// <summary>
    /// Register a producer (log tail, filesystem reconcile, …). Must be called
    /// before <see cref="StartAsync"/>. The world's merger consumes from every
    /// registered producer and merges their frames by timestamp, breaking ties
    /// by <see cref="IFrameProducer{TPayload}.Priority"/>.
    /// </summary>
    void RegisterProducer<T>(IFrameProducer<T> producer);

    /// <summary>
    /// Register a folder for a frame payload type. The world routes frames of
    /// that type to this folder. Exactly one folder per payload type
    /// (registering a second throws).
    /// </summary>
    void RegisterFolder<T>(IFolder<T> folder);

    /// <summary>
    /// Register a composer. The world dispatches
    /// <see cref="IComposer.Observe(object, IWorldClock)"/> for each input event
    /// type the composer declares, in topologically-ordered fashion within each
    /// frame's resolution (principle 11).
    /// </summary>
    void RegisterComposer(IComposer composer);

    /// <summary>
    /// Begin frame application. Closes the registration set; from here forward
    /// the world drains producers in timestamp order, dispatches to folders,
    /// resolves composers, publishes domain frames to <see cref="Bus"/>.
    /// </summary>
    Task StartAsync(CancellationToken ct);
}
