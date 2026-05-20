namespace Mithril.Shared.Logging;

/// <summary>
/// L1 of the layered log pipeline (#511 deliverable 3 / #550 — this PR
/// ships PR 1 of #550: the driver only, no consumer migration). Sits
/// between L0.5 (the classifier + splitter pair —
/// <see cref="PlayerLogClassifier"/> and <see cref="PlayerLogPipeSplitter"/>)
/// and L2 (future verb/arg recognition). L1 owns the cross-cutting concerns
/// that today are hand-rolled per consumer:
///
/// <list type="bullet">
///   <item>per-subscription <see cref="ReplayMode"/> with an explicit
///   <see cref="LogEnvelope{T}.IsReplay"/> flag on every delivered envelope
///   (capability A + B);</item>
///   <item>per-message containment via try/catch + rate-limited Warn
///   (capability C — retires the #512/#515/#522/#541 per-consumer
///   <c>ThrottledWarn</c> stopgaps in the migration PRs that follow);</item>
///   <item>per-subscription drop accounting on the
///   <see cref="LogSubscriptionDiagnostics"/> snapshot — no more silent
///   <c>DropOldest</c> (capability D);</item>
///   <item><see cref="DeliveryContext"/> — <see cref="DeliveryContext.Inline"/>
///   or <see cref="DeliveryContext.Marshaled"/> on a UI
///   <see cref="System.Windows.Threading.Dispatcher"/>, retiring the five
///   hand-rolled <c>Application.Current?.Dispatcher</c> helpers in the
///   migration PRs (capability E);</item>
///   <item>opt-in <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/>
///   filter, dropping envelopes whose payload <c>Sequence</c> is
///   <c>&lt;= HighWater</c> before delivery (capability F);</item>
///   <item>per-subscription fault state machine — N-consecutive failures
///   ⇒ <see cref="LogSubscriptionState.Degraded"/> ⇒ surfaced on
///   <see cref="Mithril.Shared.Modules.IAttentionAggregator"/> via
///   <see cref="LogStreamAttentionSource"/> (capability G).</item>
/// </list>
///
/// <para><b>Composition, not base class.</b> Consumers use the driver via
/// <see cref="Subscribe{T}"/> — they do not extend a
/// <c>LogConsumer&lt;T&gt;</c>. The archetype-B consumers have
/// heterogeneous bespoke pre-subscription setup that a template-method
/// base class would contort (Samwise loads persisted state + subscribes a
/// second source; Smaug wires character-changed events; Legolas brackets
/// on <c>IsSurveySessionActive</c>). Driver, not base. See the
/// "Composition, not base class — explicit hard rule" section of #550.</para>
/// </summary>
public interface ILogStreamDriver
{
    /// <summary>
    /// Subscribe to a typed log stream. <typeparamref name="T"/> must be one
    /// of the five supported payload types:
    /// <list type="bullet">
    ///   <item><see cref="LocalPlayerLogLine"/> — the L0.5 LocalPlayer pipe</item>
    ///   <item><see cref="CombatActorLogLine"/> — the L0.5 combat-actor pipe</item>
    ///   <item><see cref="SystemSignalLogLine"/> — the L0.5 system-signal pipe</item>
    ///   <item><see cref="IClassifiedPlayerLogLine"/> — the L0.5 unified
    ///   classified pipe (#556). Dispatch is by <c>typeof(T)</c>
    ///   exact-equality, so a <c>Subscribe&lt;LocalPlayerLogLine&gt;</c>
    ///   continues to route to the typed pipe above, NOT here, even though
    ///   <see cref="LocalPlayerLogLine"/> implements the interface. Used by
    ///   cross-pipe-ordering-sensitive consumers (Pin/Weather/Position) that
    ///   need source-Sequence ordering across LocalPlayer and SystemSignal
    ///   envelopes on a single subscription.</item>
    ///   <item><see cref="RawLogLine"/> — the L0 chat stream
    ///   (<see cref="IChatLogStream"/>). Note that Player.log
    ///   <see cref="RawLogLine"/> consumption stays on
    ///   <see cref="IPlayerLogStream"/> directly — L1 routes the L0.5
    ///   pipes, not pre-classification raw Player.log.</item>
    /// </list>
    /// </summary>
    /// <param name="handler">
    /// Invoked for every delivered envelope. The driver wraps the
    /// invocation in try/catch + rate-limited Warn so an exception
    /// thrown by the handler does NOT kill the subscription pump. The
    /// handler runs on the thread implied by
    /// <see cref="LogSubscriptionOptions.DeliveryContext"/>.
    /// </param>
    /// <param name="options">
    /// Subscription policy. Defaults preserve every pre-L1 consumer's
    /// behaviour (<see cref="ReplayMode.FromSessionStart"/>,
    /// <see cref="DeliveryContext.Inline"/>, no high-water).
    /// </param>
    /// <returns>
    /// A disposable handle. Disposing tears down the upstream subscription,
    /// resolves any pending attention entry, and is safe to call multiple
    /// times.
    /// </returns>
    ILogSubscription Subscribe<T>(
        Func<LogEnvelope<T>, ValueTask> handler,
        LogSubscriptionOptions? options = null)
        where T : class;
}
