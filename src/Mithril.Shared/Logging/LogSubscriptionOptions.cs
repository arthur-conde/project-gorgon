namespace Mithril.Shared.Logging;

/// <summary>
/// L1 per-subscription options bag (#511 deliverable 3 / #550). A consumer
/// composes the cross-cutting concerns it wants (replay policy, dispatcher
/// marshalling, idempotence filter, fault thresholds) when calling
/// <see cref="ILogStreamDriver.Subscribe{T}"/>, and L1 owns delivery
/// semantics from that point on.
///
/// <para>All values are optional; defaults preserve today's behaviour:
/// <see cref="ReplayMode.FromSessionStart"/>, <see cref="DeliveryContext.Inline"/>,
/// no high-water filter, fault threshold = 8 consecutive failures. The
/// "no consumer migration in this PR" rule (PR 1 of #550) means defaults
/// must be byte-equivalent to pre-L1 behaviour modulo the new
/// rate-limited <c>Warn</c> + drop counter + fault SM, which are inert
/// when no failure or drop occurs.</para>
/// </summary>
public sealed record LogSubscriptionOptions
{
    /// <summary>
    /// Default — exposed so callers can spread (<c>options with { ... }</c>)
    /// rather than ctor a fresh record.
    /// </summary>
    public static LogSubscriptionOptions Default { get; } = new();

    /// <summary>
    /// Backlog policy. Default <see cref="ReplayMode.FromSessionStart"/>
    /// matches every pre-L1 consumer. For chat-backed subscriptions the
    /// value is moot — the driver coerces to <see cref="ReplayMode.LiveOnly"/>
    /// and logs a one-time diagnostic if the caller asked for replay (see
    /// <see cref="ReplayMode"/> for rationale).
    /// </summary>
    public ReplayMode ReplayMode { get; init; } = ReplayMode.FromSessionStart;

    /// <summary>
    /// Thread the handler runs on. Default <see cref="DeliveryContext.Inline"/>;
    /// callers that mutate bound <c>ObservableCollection</c>s pass
    /// <see cref="DeliveryContext.Marshaled"/> with the UI dispatcher.
    /// </summary>
    public DeliveryContext DeliveryContext { get; init; } = DeliveryContext.Inline;

    /// <summary>
    /// Optional restart-safe idempotence filter. When non-null, the driver
    /// drops every envelope whose payload <c>Sequence</c> is <c>&lt;= HighWater</c>
    /// <em>before</em> handler invocation. Opt-in per the #549 audit: the four
    /// archetype-B consumers that need it take it; the others (which own
    /// per-key dedup at the sink layer or are structurally replay-immune)
    /// decline. <see cref="RawLogLine"/> (chat) lines also carry a
    /// <c>Sequence</c>; chat consumers may opt in for defence-in-depth.
    /// </summary>
    public long? SkipProcessedHighWater { get; init; }

    /// <summary>
    /// Fault state machine threshold (#550 capability G). After this many
    /// <em>consecutive</em> handler failures the subscription transitions to
    /// <see cref="LogSubscriptionState.Degraded"/>, emits one non-throttled
    /// <c>Error</c> diagnostic, keeps retrying every subsequent envelope,
    /// and surfaces on <c>IAttentionAggregator</c> via the L1 attention
    /// source. A subsequent successful delivery resolves both. Default = 8
    /// — chosen small enough to surface a stuck handler within seconds at
    /// any plausible log rate, but large enough that a transient burst of
    /// malformed lines doesn't flap the attention bus.
    /// </summary>
    public int DegradedAfterConsecutiveFailures { get; init; } = 8;

    /// <summary>
    /// Optional diagnostic category override. When null, the driver uses a
    /// stream-typed default ("Mithril.Logging.L1.LocalPlayer" etc.). Set this
    /// to the consumer's own bucket (e.g. "Samwise.Ingestion") so containment
    /// warnings about a consumer's handler land in that consumer's diag
    /// stream rather than a generic L1 stream — the post-migration shape
    /// (this PR registers the driver; the consumer-migration PRs supply
    /// their own category as they're switched over).
    /// </summary>
    public string? DiagnosticCategory { get; init; }
}
