namespace Arda.Contracts.State.Health;

/// <summary>
/// Observes per-driver health state (timestamp drift, replay/live mode,
/// frame throughput) for the Arda pipeline. Consumed by the shell status
/// bar and Palantir's diagnostics page.
/// </summary>
public interface IWorldHealthView
{
    /// <summary>Health snapshot for the Player.log driver.</summary>
    WorldHealth Player { get; }

    /// <summary>Health snapshot for the Chat log driver.</summary>
    WorldHealth Chat { get; }

    /// <summary>True when both drivers are in <see cref="WorldMode.Live"/>.</summary>
    bool AllLive { get; }

    /// <summary>
    /// Fires after any health state mutation. Consumers should re-read the
    /// <see cref="Player"/> and <see cref="Chat"/> properties.
    /// </summary>
    event EventHandler? Changed;
}
