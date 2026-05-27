namespace Arda.Dispatch;

/// <summary>
/// Runs the L2 dispatch loop: pulls <see cref="Arda.Abstractions.Logs.LogLine"/>
/// values from a source, extracts verbs, and dispatches to the L3 handler table.
/// One instance per log-source family (Player, Chat).
/// </summary>
public interface IWorldDriver
{
    /// <summary>
    /// Run the dispatch loop until cancellation. Pulls lines from the
    /// source, extracts verbs, dispatches to handlers.
    /// </summary>
    Task RunAsync(CancellationToken ct);
}
