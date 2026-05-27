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
    /// Non-null when the Arda pipeline has halted on a grammar drift. The shell
    /// surfaces a blocking banner in this state; module tabs continue to render
    /// whatever state they accumulated pre-halt but no new events arrive.
    /// </summary>
    GrammarBreak? Break { get; }

    /// <summary>True when <see cref="Break"/> is non-null.</summary>
    bool IsHalted { get; }

    /// <summary>
    /// Fires after any health state mutation. Consumers should re-read the
    /// <see cref="Player"/>, <see cref="Chat"/>, and <see cref="Break"/> properties.
    /// </summary>
    event EventHandler? Changed;
}
