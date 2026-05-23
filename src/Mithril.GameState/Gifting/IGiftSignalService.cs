using Mithril.Shared.Logging;

namespace Mithril.GameState.Gifting;

/// <summary>
/// Tier-2 signature service for accepted player gifts. Emits a
/// <see cref="GiftAccepted"/> event whenever the local player gives an item
/// to an NPC and the NPC accepts — synthesised from the L1 verb pair
/// (<c>ProcessDeleteItem(instanceId)</c>,
/// <c>ProcessDeltaFavor(npcKey, delta)</c>) inside an active
/// <c>ProcessStartInteraction</c> window. The state machine handles both
/// arrival orders (DeleteItem-first and DeltaFavor-first).
///
/// <para><b>Architectural position.</b> This is the first sibling under the
/// Tier-2 signature umbrella
/// (<a href="https://github.com/moumantai-gg/mithril/issues/594">#594</a>);
/// the naming convention <c>I&lt;Domain&gt;SignalService</c> locks here. The
/// in-Arwen verb-sequence correlator
/// (<c>CalibrationService._activeNpcKey</c> / <c>_pendingDeletedItem</c> /
/// <c>_pendingDelta</c>) lifts here verbatim — Arwen's consumer migration is a
/// separate downstream issue (<a href="https://github.com/moumantai-gg/mithril/issues/582">#582</a>
/// reframes onto consuming this service).</para>
///
/// <para><b>Three channels (per <a href="https://github.com/moumantai-gg/mithril/pull/584">#584</a>).</b></para>
/// <list type="bullet">
///   <item><b>React</b> — <see cref="Subscribe"/> delivers the full
///   <see cref="GiftAccepted"/> stream. Default
///   <see cref="ReplayMode.FromSessionStart"/> atomically replays every
///   resolved gift event from session start (in original order) before
///   delivering live events; <see cref="ReplayMode.LiveOnly"/> skips the
///   replay. Same shape the legacy <c>IInventoryView.Subscribe</c> shim
///   adopted under #585 (the shim itself retired in #659).</item>
///   <item><b>Query</b> — none today. No identified v1 consumer needs a
///   point-in-time snapshot ("what gifts have landed?"); the React channel
///   covers the use case. Can be added later if a consumer surfaces.</item>
///   <item><b>Bind</b> — none today. Same reasoning as Query. Consumers
///   wanting an observable collection can wrap <see cref="Subscribe"/> at the
///   call site (Palantir's <c>LiveInventoryViewModel</c> binds directly to
///   <see cref="Mithril.GameState.Inventory.IInventoryView.Items"/> instead).</item>
/// </list>
///
/// <para><b>Frame-determinism (per <a href="https://github.com/moumantai-gg/mithril/issues/594">#594</a>).</b>
/// All five verbs the SM consumes flow through a single
/// <see cref="LocalPlayerLogLine"/> subscription with
/// <see cref="ReplayMode.FromSessionStart"/> and
/// <see cref="DeliveryContext.Inline"/>. The service maintains its OWN
/// <c>instanceId → InternalName</c> map (populated from
/// <c>ProcessAddItem</c> on the same subscription) and resolves
/// <see cref="GiftAccepted.ItemInternalName"/> from that map — never via
/// <see cref="Mithril.GameState.Inventory.IInventoryView.TryResolve"/>. This is
/// load-bearing: routing the DeleteItem half through <c>IInventoryView</c>
/// would re-introduce the cross-pump race documented in
/// <a href="https://github.com/moumantai-gg/mithril/issues/582">#582</a>'s
/// <i>Replay correctness</i> section, making the lift worse than the status
/// quo.</para>
///
/// <para><b>Out-of-scope, intentionally.</b></para>
/// <list type="bullet">
///   <item><b>Stack size on the event.</b> The consumer (Arwen) resolves
///   stack size via <see cref="Mithril.GameState.Inventory.IInventoryView.TryGetStackSize"/>
///   at observation-record time — the view retains last-known sizes for
///   deleted entries specifically for this lookup path. See
///   <see cref="GiftAccepted"/> remarks.</item>
///   <item><b>Persistence.</b> Session-fresh — the React channel replays from
///   session start on every Mithril attach. Consumers (Arwen's
///   <c>observations.json</c>) persist downstream.</item>
///   <item><b>Gift refusals.</b> The NPC-rejects-the-item path is a different
///   verb sequence (no DeltaFavor); a future signature if a consumer asks.</item>
///   <item><b>SM behavioural fixes.</b> The lifted SM has a known
///   interloper-DeltaFavor quirk preserved verbatim — a non-gift DeltaFavor
///   arriving inside an active interaction will be claimed by the next
///   DeleteItem. See <see cref="GiftSignalService"/> remarks and
///   <a href="https://github.com/moumantai-gg/mithril/issues/596">#596</a>
///   <i>Out of scope → SM behavioural fixes</i>. Candidate refinements (a
///   <c>ProcessPromptForItem("Give Gift", …)</c> discriminator, a DeltaFavor
///   staleness window) are deferred to a follow-up.</item>
/// </list>
/// </summary>
public interface IGiftSignalService
{
    /// <summary>
    /// React-channel subscription. Attach a handler that receives every
    /// <see cref="GiftAccepted"/> event the service emits.
    ///
    /// <para><b>Default replay shape (<see cref="ReplayMode.FromSessionStart"/>).</b>
    /// On attach, the service atomically replays the full in-session
    /// <see cref="GiftAccepted"/> log to the handler (every event emitted
    /// since startup, in original order) before delivering live events. The
    /// replay and live-attach happen under the same internal lock the
    /// emission path holds, so a live event arriving during attach either
    /// fires after the replay finishes (and is delivered to the live
    /// handler) or fires before (and is in the replay) — never both, never
    /// neither.</para>
    ///
    /// <para><b>Live-only subscriptions.</b>
    /// <see cref="ReplayMode.LiveOnly"/> skips the replay. Only events fired
    /// after the subscription is established are delivered.
    /// <see cref="ReplayMode.SinceSubscribe"/> is treated like
    /// <see cref="ReplayMode.LiveOnly"/> — same shape the legacy
    /// <c>InventoryView.Subscribe</c> shim used before #659 retired it.</para>
    ///
    /// <para><b>Threading.</b> The handler is invoked synchronously under
    /// the internal lock both during replay (on the subscribing thread) and
    /// during live dispatch (on the L1 pump thread). Subscribers doing
    /// non-trivial work should marshal off-thread immediately.</para>
    ///
    /// <para>Dispose the returned token to stop receiving further events.</para>
    /// </summary>
    /// <param name="handler">Event handler. Must not be null.</param>
    /// <param name="replay">Backlog policy. Default
    /// <see cref="ReplayMode.FromSessionStart"/> replays the full session log;
    /// <see cref="ReplayMode.LiveOnly"/> skips it.</param>
    IDisposable Subscribe(
        Action<GiftAccepted> handler,
        ReplayMode replay = ReplayMode.FromSessionStart);
}
