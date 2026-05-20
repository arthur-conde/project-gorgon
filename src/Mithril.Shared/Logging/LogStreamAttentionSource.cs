using Mithril.Shared.Modules;

namespace Mithril.Shared.Logging;

/// <summary>
/// L1 attention source (#550 capability G). Aggregates every degraded
/// subscription on a registered <see cref="ILogStreamDriver"/> so the
/// shell's attention bus surfaces "alive but visibly degraded" as a
/// non-throttled signal — converting the pre-L1 "throttled into silence"
/// failure mode into a UI-visible one.
///
/// <para><see cref="ModuleId"/> is the shared bucket "logging" so a
/// degraded Samwise subscription and a degraded Pippin subscription both
/// surface on the same chip. Per-consumer attention (e.g. "Arwen — gifts
/// pending") stays on each module's own <see cref="IAttentionSource"/>;
/// L1's source is specifically for the subscription-health concern that's
/// shared across consumers.</para>
///
/// <para>Registered as a singleton, picked up by the existing
/// <see cref="AttentionAggregator"/> via the standard
/// <c>IEnumerable&lt;IAttentionSource&gt;</c> ctor parameter.</para>
/// </summary>
public sealed class LogStreamAttentionSource : IAttentionSource, IDisposable
{
    /// <summary>Module id key used by the attention aggregator.</summary>
    public const string SourceId = "logging";

    private readonly object _gate = new();
    private readonly HashSet<string> _degradedSubscriptions = new(StringComparer.Ordinal);

    public string ModuleId => SourceId;
    public string DisplayLabel => "Logging — degraded subscriptions";

    public int Count
    {
        get { lock (_gate) return _degradedSubscriptions.Count; }
    }

    public event EventHandler? Changed;

    /// <summary>
    /// Called by the driver when a subscription enters
    /// <see cref="LogSubscriptionState.Degraded"/>. Idempotent — a second
    /// call for the same id is a no-op.
    /// </summary>
    internal void NotifyDegraded(string subscriptionId)
    {
        bool changed;
        lock (_gate) changed = _degradedSubscriptions.Add(subscriptionId);
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the driver when a previously degraded subscription
    /// returns to <see cref="LogSubscriptionState.Healthy"/>. Idempotent.
    /// </summary>
    internal void NotifyHealthy(string subscriptionId)
    {
        bool changed;
        lock (_gate) changed = _degradedSubscriptions.Remove(subscriptionId);
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        // Subscriptions hold a reference back to us through the driver, so
        // disposal-during-shutdown is the only path that matters — we just
        // clear the set so any late callback is a no-op.
        lock (_gate) _degradedSubscriptions.Clear();
    }
}
