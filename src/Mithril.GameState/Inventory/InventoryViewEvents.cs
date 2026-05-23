namespace Mithril.GameState.Inventory;

/// <summary>
/// View-emitted event for a newly-added inventory entry (#602). Composed: the
/// view pairs <see cref="PlayerInventoryAdded"/> from the PlayerWorld bus with
/// a matching <see cref="ChatInventoryObserved"/> from the ChatWorld bus
/// within a TTL window to carry stack size. Unpaired adds (no chat
/// correlation, no other confirming signal) still fire — with
/// <see cref="SizeConfirmed"/> = false and an unconfirmed default size of 1 —
/// so consumers see every instance that enters inventory.
///
/// <para><b>Naming.</b> Follows #657 — past-tense participle, no
/// <c>Event</c> suffix, NO world prefix on view-emitted events. View events
/// flow on the <see cref="IInventoryView"/>'s own typed bus (see the
/// composition spec in <c>docs/world-simulator.md</c> §Worked example 1).</para>
/// </summary>
/// <param name="InstanceId">PG's per-instance unique id (from
/// <c>ProcessAddItem</c>).</param>
/// <param name="InternalName">Reference-data <c>InternalName</c> key for the
/// item.</param>
/// <param name="StackSize">Stack size at the moment of the event. Authoritative
/// when <see cref="SizeConfirmed"/> is <c>true</c>; the unconfirmed default of
/// 1 when not yet paired.</param>
/// <param name="SizeConfirmed">Whether <see cref="StackSize"/> came from an
/// authoritative source (chat correlation, non-stackable reference data,
/// export seed). The <see cref="IInventoryView.TryGetStackSize"/> contract
/// keys on this bit.</param>
/// <param name="Timestamp">UTC timestamp of the originating Player.log
/// <c>ProcessAddItem</c> line.</param>
public readonly record struct InventoryItemAdded(
    long InstanceId,
    string InternalName,
    int StackSize,
    bool SizeConfirmed,
    DateTime Timestamp);

/// <summary>
/// View-emitted event for a removed inventory entry (#602). Passthrough of
/// <see cref="PlayerInventoryRemoved"/> — chat has no removal signal, so the
/// view simply forwards the player-side observation onto its own bus. The
/// passthrough is justified by the #643 ratification's "member of a multi-event
/// composed surface" clause (see <c>docs/world-simulator.md</c> §Decisions
/// ratified post-#642): the view's three-channel inventory surface (Added /
/// StackChanged / Removed) is coherent as a unit even though Removed alone
/// would be a pure relabeling.
/// </summary>
public readonly record struct InventoryItemRemoved(
    long InstanceId,
    string InternalName,
    int StackSize,
    bool SizeConfirmed,
    DateTime Timestamp);

/// <summary>
/// View-emitted event for a stack-size change on an already-tracked instance
/// (#602). Composed from any of:
/// <list type="bullet">
///   <item><see cref="PlayerInventoryStackUpdated"/> from PlayerWorld
///   (<c>ProcessUpdateItemCode</c> / <c>ProcessRemoveFromStorageVault</c>) —
///   authoritative size update for an existing id.</item>
///   <item>A late <see cref="ChatInventoryObserved"/> matching a previously-
///   defaulted <c>InventoryItemAdded</c> within the correlator's TTL — back-
///   fills the size and promotes <see cref="SizeConfirmed"/> from false to
///   true.</item>
/// </list>
/// </summary>
public readonly record struct InventoryStackChanged(
    long InstanceId,
    string InternalName,
    int StackSize,
    bool SizeConfirmed,
    DateTime Timestamp);
