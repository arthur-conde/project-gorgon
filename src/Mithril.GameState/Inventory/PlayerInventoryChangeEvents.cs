using Mithril.WorldSim;

namespace Mithril.GameState.Inventory;

/// <summary>
/// Folder-emitted change event for a Player.log <c>ProcessAddItem</c>
/// observation (#602). Carries the instance-id ledger entry's <c>InternalName</c>
/// only — Player.log does not carry a stack-size for this verb, so quantity is
/// composed at the view layer from the matching <see cref="ChatInventoryObserved"/>
/// observation if any (per design notebook §Worked example 1).
///
/// <para>Naming follows the #657 ratification: past-tense participle, no
/// <c>Event</c> suffix, mandatory <c>Player</c> world prefix on folder-emitted
/// events. Subscribers on the PlayerWorld bus consume via
/// <c>Bus.Subscribe&lt;PlayerInventoryAdded&gt;(...)</c>.</para>
/// </summary>
/// <param name="InstanceId">PG's per-instance unique id (the integer in
/// <c>ProcessAddItem(InternalName(id), …)</c>). Stable across the item's
/// lifetime in inventory; reused by PG only after the item is deleted.</param>
/// <param name="InternalName">Reference-data <c>InternalName</c> key (e.g.
/// <c>BarleySeeds</c>).</param>
/// <param name="Timestamp">UTC timestamp of the source <c>ProcessAddItem</c>
/// line. Carried explicitly because folder consumers (chiefly the view layer's
/// correlator) need the in-game timeline, not the wall-clock.</param>
public readonly record struct PlayerInventoryAdded(
    long InstanceId,
    string InternalName,
    DateTime Timestamp) : IChangeEvent;

/// <summary>
/// Folder-emitted change event for a Player.log <c>ProcessDeleteItem</c>
/// observation (#602). Mirrors <see cref="PlayerInventoryAdded"/>; the
/// view layer pairs this with the prior <c>InternalName</c> from its ledger.
/// </summary>
/// <param name="InstanceId">Same id PG emitted on the corresponding
/// <c>ProcessAddItem</c>. The folder retains the id-to-name mapping after the
/// remove so late <see cref="IPlayerInventoryState.TryResolve"/> callers
/// (e.g. Arwen's gift-attribution path) still see it.</param>
/// <param name="InternalName">Last-known <c>InternalName</c> for the
/// instance, from the folder's ledger. Empty string if the instance was never
/// added in this session — PG can emit a delete for a carryover instance.</param>
/// <param name="Timestamp">UTC timestamp of the source <c>ProcessDeleteItem</c>
/// line.</param>
public readonly record struct PlayerInventoryRemoved(
    long InstanceId,
    string InternalName,
    DateTime Timestamp) : IChangeEvent;

/// <summary>
/// Folder-emitted change event for a Player.log signal that carries an
/// authoritative stack size for an existing instance (#602). The two
/// Player.log surfaces this represents are <c>ProcessUpdateItemCode(id, code,
/// _)</c> — where <c>(code &gt;&gt; 16) + 1</c> is the post-event size — and
/// <c>ProcessRemoveFromStorageVault(_, _, id, N)</c> where <c>N</c> is the
/// literal stack size for a vault-to-bag transfer.
///
/// <para>Per the design notebook spec (§Migration path #1, §Worked example 1)
/// the architectural primitive is "Player.log half: instance-id-keyed ledger,
/// no quantities" — but PG's verbs DO carry authoritative stack-size deltas
/// for already-tracked instances on these two verbs. Carrying them on a
/// separate folder-emitted change event lets the view layer pick up the
/// stack-size update without the PlayerWorld folder owning a stack-size column
/// in its primary ledger. See §"Decisions ratified post-#642" — the bus
/// surface admits multiple change-event types per folder; this is the
/// stack-update channel of the inventory folder's three-channel surface.</para>
/// </summary>
/// <param name="InstanceId">Instance id whose stack size is being updated.</param>
/// <param name="InternalName">Last-known <c>InternalName</c> for the
/// instance, from the folder's ledger. Empty string if not seen this
/// session.</param>
/// <param name="StackSize">Authoritative post-event stack size. Always
/// <c>&gt; 0</c> for the two source verbs.</param>
/// <param name="Timestamp">UTC timestamp of the source log line.</param>
public readonly record struct PlayerInventoryStackUpdated(
    long InstanceId,
    string InternalName,
    int StackSize,
    DateTime Timestamp) : IChangeEvent;
