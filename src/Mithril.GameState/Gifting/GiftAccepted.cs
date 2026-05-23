namespace Mithril.GameState.Gifting;

/// <summary>
/// Tier-2 domain event emitted by <see cref="IGiftSignalService"/> when the
/// local player gives an item to an NPC and the NPC accepts. Synthesized from
/// the L1 verb pair (<c>ProcessDeleteItem(instanceId)</c>,
/// <c>ProcessDeltaFavor(npcKey, delta)</c>) inside an active
/// <c>ProcessStartInteraction</c> window — resolution fires at the later of
/// the two verbs regardless of arrival order.
///
/// <para><b>Three load-bearing timestamps are carried, not derived:</b></para>
/// <list type="bullet">
///   <item><see cref="Timestamp"/> — the <c>ProcessDeltaFavor</c> log-line
///   timestamp. This is the resolve point, matching today's Arwen
///   <c>RecordObservation(... timestamp)</c> behaviour (the DeltaFavor stamp
///   wins regardless of whether DeleteItem or DeltaFavor arrived first).</item>
///   <item><see cref="InteractionStartedAt"/> — the
///   <c>ProcessStartInteraction</c> log-line timestamp captured at
///   window-open. Threaded through to emission so downstream attribution can
///   correlate gifts across an interaction window. New field vs Arwen's
///   in-place SM, which dropped the StartInteraction timestamp.</item>
///   <item>(<see cref="ItemInstanceId"/> / <see cref="ItemInternalName"/>
///   together identify the gifted item.) The <see cref="ItemInternalName"/>
///   is resolved from the signature service's OWN <c>instanceId →
///   InternalName</c> map — populated by its own <c>ProcessAddItem</c>
///   ingestion — never via a cross-pump call to
///   <see cref="Mithril.GameState.Inventory.IInventoryView.TryResolve"/>. This is the Tier-2 frame-determinism
///   commitment: a Tier-2 service consumes only L1 directly so the cross-pump
///   race documented in <a href="https://github.com/moumantai-gg/mithril/issues/582">#582</a>
///   can't re-appear at a different scope.</item>
/// </list>
///
/// <para><b>Stack-size resolution is the consumer's job</b>, intentionally —
/// <see cref="ItemInstanceId"/> + <see cref="ItemInternalName"/> are
/// sufficient for a downstream consumer (Arwen) to look up the pre-delete
/// stack size via <see cref="Mithril.GameState.Inventory.IInventoryView.TryGetStackSize"/>
/// at observation-record time. Hoisting stack size onto the event here would
/// inherit the view's chat-correlation race without giving the consumer
/// any new lever, so the boundary stays at "item identity, item value, favor
/// delta, timestamps". See issue
/// <a href="https://github.com/moumantai-gg/mithril/issues/596">#596</a>
/// "Stack-size resolution" out-of-scope note.</para>
/// </summary>
public readonly record struct GiftAccepted(
    string NpcKey,
    long ItemInstanceId,
    string ItemInternalName,
    double DeltaFavor,
    DateTimeOffset Timestamp,
    DateTimeOffset InteractionStartedAt);
