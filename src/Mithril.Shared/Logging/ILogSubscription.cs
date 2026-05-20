namespace Mithril.Shared.Logging;

/// <summary>
/// L1 subscription handle (#511 deliverable 3 / #550). Returned by
/// <see cref="ILogStreamDriver.Subscribe{T}"/>; disposing tears down the
/// underlying upstream subscription, completes the handler-pump task, and
/// (if degraded) resolves the consumer's attention entry.
///
/// <para>The handle exposes the live diagnostic counters
/// (<see cref="Diagnostics"/>) so a consumer that wants visibility into its
/// own L1 subscription health — a "did we stall?" dev panel, a regression
/// test, or the future shell-side diagnostics panel — can read them
/// without going through the diagnostic-sink log stream.</para>
/// </summary>
public interface ILogSubscription : IDisposable
{
    /// <summary>
    /// Stable id assigned at subscription time. Used to key the
    /// subscription's attention entry; surfaces in diagnostic messages so
    /// a degraded subscription is identifiable even when multiple
    /// subscriptions share a category.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Snapshot of the live counters. See <see cref="LogSubscriptionDiagnostics"/>.
    /// </summary>
    LogSubscriptionDiagnostics Diagnostics { get; }

    /// <summary>
    /// Raised when the fault state transitions
    /// (<see cref="LogSubscriptionState.Healthy"/> ⇔
    /// <see cref="LogSubscriptionState.Degraded"/>). The attention source
    /// listens to this internally; consumers may also subscribe for their
    /// own UI feedback. Payload-free; read <see cref="Diagnostics"/>.
    /// </summary>
    event EventHandler? StateChanged;
}
