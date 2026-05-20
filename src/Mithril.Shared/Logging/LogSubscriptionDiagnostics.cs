namespace Mithril.Shared.Logging;

/// <summary>
/// L1 per-subscription diagnostic counters surface (#550 capability D + G).
/// A snapshot of the subscription's accounting at the moment of read.
/// Today's <c>Channel.CreateBounded&lt;T&gt;(... DropOldest ...)</c> at L0 /
/// L0.5 drops silently when a subscriber's channel fills; L1 surfaces the
/// drop count here so a consumer can pin "did we stall?" against zero
/// instead of guessing. Pair with the <see cref="LogSubscriptionState"/>
/// for fault-SM observability.
///
/// <para>Fields:
/// <list type="bullet">
///   <item><c>Delivered</c> — lines delivered to the consumer's handler
///   (post-filter, post-marshal).</item>
///   <item><c>Dropped</c> — lines dropped because the consumer's bounded
///   channel filled (DropOldest).</item>
///   <item><c>HandlerFailures</c> — envelopes the consumer's handler
///   threw on (rate-limited Warn'd, retry-continued).</item>
///   <item><c>ConsecutiveFailures</c> — consecutive handler failures
///   right now. Reset to 0 on the next successful delivery.</item>
///   <item><c>HighWaterSkipped</c> — lines skipped by the
///   <c>SkipProcessedHighWater</c> filter (if configured).</item>
///   <item><c>State</c> — current fault state.
///   <see cref="LogSubscriptionState.Degraded"/> ⇒ surfaced on the
///   attention bus.</item>
/// </list>
/// </para>
///
/// <para>The counters are reads of <see cref="System.Threading.Interlocked"/>
/// monotonic counters under the hood, so a snapshot taken concurrently with
/// the driver pump is a valid lower bound; a quiescent snapshot (after the
/// driver has finished draining and the consumer awaits) is exact.</para>
/// </summary>
public readonly record struct LogSubscriptionDiagnostics(
    long Delivered,
    long Dropped,
    long HandlerFailures,
    int ConsecutiveFailures,
    long HighWaterSkipped,
    LogSubscriptionState State);
