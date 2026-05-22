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
    /// before <see cref="StartMerger"/>. The world's merger consumes from every
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
    /// Close the registration set and begin merger drain. Returns a long-running
    /// task that completes when every producer is drained or <paramref name="ct"/>
    /// is cancelled.
    ///
    /// <para>The contract distinguishes two phases:</para>
    /// <list type="number">
    ///   <item><b>Registration acceptance</b> — every call to
    ///   <see cref="RegisterProducer{T}"/>, <see cref="RegisterFolder{T}"/>,
    ///   and <see cref="RegisterComposer"/> MUST happen before <c>StartMerger</c>
    ///   is invoked. The registration set seals on entry; further attempts
    ///   throw.</item>
    ///   <item><b>Merger running</b> — the returned task is the long-running
    ///   drain. The caller — the trailing <c>WorldMergerStartHostedService</c>
    ///   registered LAST in the composition (#696 Call 2) — fires this from
    ///   its own <c>IHostedService.StartAsync</c> WITHOUT awaiting completion
    ///   so host startup returns immediately, and awaits the task during
    ///   graceful shutdown.</item>
    /// </list>
    ///
    /// <para>This signature deliberately differs from
    /// <see cref="Microsoft.Extensions.Hosting.BackgroundService.ExecuteAsync"/> —
    /// see issue #696. <c>BackgroundService</c> schedules <c>ExecuteAsync</c> at
    /// a non-deterministic moment relative to other hosted services'
    /// <c>StartAsync</c> calls, which is the entire failure mode this contract
    /// corrects. The merger must start strictly AFTER every other hosted service
    /// has finished its registration work (bus subscriptions, folder/producer
    /// wiring), and the trailing-registration composition enforces that
    /// structurally rather than by discipline.</para>
    /// </summary>
    Task StartMerger(CancellationToken ct);
}
