namespace Mithril.WorldSim;

/// <summary>
/// One of three state-machine kinds (principle 10). Folders consume frames and
/// emit change events. One frame is dispatched to exactly one folder, determined
/// by the frame's payload type. Folders mutate world state; they live inside one
/// world's runtime.
/// </summary>
/// <typeparam name="TPayload">
/// The frame payload type this folder accepts. A world has at most one folder
/// per payload type (registering a second throws — see
/// <see cref="IWorld.RegisterFolder{T}"/>).
/// </typeparam>
public interface IFolder<TPayload>
{
    /// <summary>
    /// Apply one frame to internal state. Returns the change events the mutation
    /// produced (empty if no observable change). Mutations must depend only on
    /// the frame's payload and the folder's own prior state. Folders never read
    /// <see cref="DateTime.UtcNow"/> or any other real-time source; they use
    /// <paramref name="clock"/> for "now" so the application is replay-
    /// deterministic (principle 5 + principle 13).
    /// </summary>
    IReadOnlyList<IChangeEvent> Apply(Frame<TPayload> frame, IWorldClock clock);
}
