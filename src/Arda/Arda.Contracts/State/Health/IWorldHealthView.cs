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
    /// Non-null when at least one grammar break has been observed this session
    /// (either via halt or via tolerant-mode observation). The shell uses this
    /// to populate banner copy.
    /// </summary>
    GrammarBreak? Break { get; }

    /// <summary>
    /// True when the Arda pipeline has halted on a grammar drift (default
    /// mode). The shell surfaces a blocking halt banner in this state.
    /// </summary>
    bool IsHalted { get; }

    /// <summary>
    /// True when the pipeline has observed at least one grammar break but is
    /// running in tolerant mode (<c>MITHRIL_GRAMMAR_TOLERANT=1</c>) and is
    /// therefore not halted. The shell surfaces an amber warning banner to
    /// keep the developer honest about what the env var is hiding.
    /// </summary>
    bool IsTolerantBreakActive { get; }

    /// <summary>
    /// Total breaks observed this session (halt + tolerant combined). Drives
    /// the "{N} parse failures so far" copy in the tolerant banner.
    /// </summary>
    int ObservedBreakCount { get; }

    /// <summary>
    /// Fires after any health state mutation. Consumers should re-read the
    /// <see cref="Player"/>, <see cref="Chat"/>, <see cref="Break"/>,
    /// <see cref="IsHalted"/>, <see cref="IsTolerantBreakActive"/>, and
    /// <see cref="ObservedBreakCount"/> properties.
    /// </summary>
    event EventHandler? Changed;
}
