using Mithril.Shared.Logging;

namespace Mithril.GameState.Inventory;

/// <summary>
/// A live inventory transition. <paramref name="InstanceId"/> is the per-item
/// unique id emitted by <c>ProcessAddItem</c> / <c>ProcessDeleteItem</c>.
/// <paramref name="InternalName"/> maps to
/// <see cref="Mithril.Reference.Models.Items.Item.InternalName"/>.
/// <paramref name="Timestamp"/> is the source log line's timestamp (UTC), not
/// wall-clock — consumers with time-window logic (e.g. Samwise's plant-resolve
/// window) need the in-game timeline.
/// <paramref name="StackSize"/> is the stack size at the moment of the event:
/// for <see cref="InventoryEventKind.Added"/> it's the size known when the
/// item entered the map (1 if no chat correlation has landed yet);
/// for <see cref="InventoryEventKind.Deleted"/> it's the last-known size
/// before removal; for <see cref="InventoryEventKind.StackChanged"/> it's
/// the new size.
/// <paramref name="SizeConfirmed"/> is the same bit
/// <see cref="IInventoryService.TryGetStackSize"/> consults — false when the
/// <paramref name="StackSize"/> is the unconfirmed default-1 (a session-replayed
/// AddItem with no chat correlation), true when an authoritative source
/// (chat, UpdateItemCode, vault, export seed/reconcile) has spoken.
/// <see cref="InventoryEventKind.StackChanged"/> events always carry
/// <c>SizeConfirmed = true</c>.
/// </summary>
public readonly record struct InventoryEvent(
    InventoryEventKind Kind,
    long InstanceId,
    string InternalName,
    DateTime Timestamp,
    int StackSize,
    bool SizeConfirmed = false);

public enum InventoryEventKind
{
    Added,
    Deleted,
    /// <summary>
    /// Fired when the stack size of an existing entry changes in place
    /// (split, merge, plant-consume, vault transfer, chat back-fill of a
    /// previously-defaulted size). Not fired on Add or Delete — those carry
    /// their own size on the event.
    /// </summary>
    StackChanged,
}

/// <summary>
/// Canonical <c>instanceId → InternalName</c> lookup maintained by tailing
/// <c>ProcessAddItem</c> / <c>ProcessDeleteItem</c> / <c>ProcessUpdateItemCode</c>
/// / <c>ProcessRemoveFromStorageVault</c> on <c>IPlayerLogStream</c>, plus the
/// chat <c>[Status]</c> channel via <c>IChatLogStream</c> for stack-size
/// correlation on fresh additions.
///
/// <para><b>Three channels (per <a href="https://github.com/moumantai-gg/mithril/pull/584">#584</a>'s
/// service-design rule).</b></para>
/// <list type="bullet">
///   <item><b>Query</b> — <see cref="TryResolve"/> and
///   <see cref="TryGetStackSize"/> answer "what's in inventory right now?"
///   (including last-known state for deleted entries — Arwen's
///   gift-attribution path needs the pre-delete name/size).</item>
///   <item><b>React</b> — <see cref="Subscribe(Action{InventoryEvent}, ReplayMode)"/>
///   delivers the full session <see cref="InventoryEvent"/> log. The default
///   <see cref="ReplayMode.FromSessionStart"/> replays every Added / Deleted /
///   StackChanged event the service has emitted in this session (in order)
///   before going live; <see cref="ReplayMode.LiveOnly"/> skips the replay
///   for consumers that genuinely don't care about history. The contract
///   matches <see cref="Mithril.Shared.Logging.ILogStreamDriver"/>'s
///   default replay-then-live shape.</item>
///   <item><b>Bind</b> — not exposed today. Consumers that want an
///   observable <c>Items</c> collection wrap <see cref="Subscribe"/> at the
///   call site (see <c>Palantir.LiveInventoryViewModel</c>).</item>
/// </list>
///
/// <para>Owning this centrally (rather than having each module rebuild its
/// own map from the stream) avoids the late-subscribe race: modules either
/// query the live map at use-time via <see cref="TryResolve"/> or
/// <see cref="TryGetStackSize"/>, or subscribe via <see cref="Subscribe"/>
/// which atomically replays the full event log to the new handler before
/// delivering live events.</para>
/// </summary>
public interface IInventoryService
{
    /// <summary>
    /// Resolve an instance id to its internal item name, if known. Returns
    /// true even for ids that have been deleted — the entry is retained so
    /// late lookups (e.g. Arwen's gift-attribution path) still succeed.
    /// </summary>
    bool TryResolve(long instanceId, out string internalName);

    /// <summary>
    /// Resolve an instance id to its current stack size, if and only if the
    /// size has been *confirmed* by an authoritative source — chat correlation,
    /// <c>ProcessUpdateItemCode</c>, <c>ProcessRemoveFromStorageVault</c>,
    /// export seeding, or export reconcile. Returns false when:
    /// <list type="bullet">
    ///   <item>The instance has never been observed (carryover from a prior
    ///   game session whose AddItem isn't in this log's replay buffer).</item>
    ///   <item>The instance is in the map but its size is still the
    ///   unconfirmed default of 1 (a session-replayed AddItem with no
    ///   chat status in the correlation window). A real stack of 1 is
    ///   distinguishable: the chat status confirms it, the corresponding
    ///   AddItem path flips confirmation on, and TryGetStackSize reports
    ///   <c>true, 1</c>.</item>
    /// </list>
    /// Like <see cref="TryResolve"/>, returns the last-known confirmed size
    /// for entries that have been deleted in this session — Arwen's
    /// gift-attribution path needs the pre-delete size.
    /// </summary>
    bool TryGetStackSize(long instanceId, out int stackSize);

    /// <summary>
    /// React-channel subscription. Attach a handler that receives every
    /// <see cref="InventoryEvent"/> the service emits.
    ///
    /// <para><b>Default replay shape (<see cref="ReplayMode.FromSessionStart"/>).</b>
    /// On attach, the service replays the full in-session event log to the
    /// handler (every Added / Deleted / StackChanged event it has emitted
    /// since startup, in original order) before delivering live events.
    /// This is the same contract <see cref="Mithril.Shared.Logging.ILogStreamDriver"/>
    /// offers for L1 subscriptions and resolves the late-subscribe class of
    /// bug surfaced by audits <a href="https://github.com/moumantai-gg/mithril/issues/579">#579</a>
    /// / <a href="https://github.com/moumantai-gg/mithril/issues/588">#588</a>:
    /// a consumer attaching after some items have been added-and-deleted
    /// during this session now receives the Deleted (and any interim
    /// StackChanged) events for those items, not just the survivors.</para>
    ///
    /// <para><b>Live-only subscriptions.</b> Callers that genuinely don't
    /// care about session history (e.g. a UI that only renders events from
    /// "now" forward) pass <see cref="ReplayMode.LiveOnly"/>. Replay is
    /// skipped; the handler only sees events fired after the subscription
    /// is established.</para>
    ///
    /// <para><b>Atomicity.</b> Replay and live-attach are atomic — under an
    /// internal lock — so no event is lost, duplicated, or reordered
    /// relative to the canonical map. A live event firing during another
    /// handler's replay either runs after replay completes (and is
    /// delivered live) or runs before (and is in the replay), never both
    /// and never neither.</para>
    ///
    /// <para><b>Threading.</b> The handler is invoked synchronously under
    /// the internal lock both during replay (on the subscribing thread) and
    /// during live dispatch (on the ingestion-loop thread). Subscribers
    /// that do non-trivial work should dispatch off-thread immediately to
    /// avoid blocking ingestion.</para>
    ///
    /// <para><b>Query vs. React.</b> For "what's in inventory right now?"
    /// use the Query channel — <see cref="TryResolve"/> /
    /// <see cref="TryGetStackSize"/>. Subscribe is the React channel: a
    /// log of what happened, in order, including the Deleted events the
    /// pre-#585 contract silently dropped for fresh subscribers.</para>
    ///
    /// <para>Dispose the returned subscription to stop receiving further
    /// events.</para>
    /// </summary>
    /// <param name="handler">Event handler. Must not be null.</param>
    /// <param name="replay">Backlog policy. Default
    /// <see cref="ReplayMode.FromSessionStart"/> replays the full session
    /// event log atomically before going live;
    /// <see cref="ReplayMode.LiveOnly"/> skips the replay.</param>
    [Obsolete("Subscribe to Frame<InventoryItemAdded> et al. on IInventoryView.Bus. Cleanup tracked in #659.")]
    IDisposable Subscribe(
        Action<InventoryEvent> handler,
        ReplayMode replay = ReplayMode.FromSessionStart);
}
