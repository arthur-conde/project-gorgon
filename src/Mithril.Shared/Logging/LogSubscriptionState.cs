namespace Mithril.Shared.Logging;

/// <summary>
/// L1 per-subscription fault state (#550 capability G).
/// <see cref="Healthy"/> covers "no failures" and "transient failure
/// throttled to a Warn"; <see cref="Degraded"/> means the consecutive-
/// failure count crossed the configured threshold and the subscription
/// is surfaced on <c>IAttentionAggregator</c> until a delivery succeeds.
/// </summary>
public enum LogSubscriptionState
{
    Healthy = 0,
    Degraded = 1,
}
