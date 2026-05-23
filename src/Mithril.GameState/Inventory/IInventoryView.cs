using Mithril.Shared.Collections;
using Mithril.WorldSim;

namespace Mithril.GameState.Inventory;

/// <summary>
/// Canonical inventory surface for modules (#602). Composes the PlayerWorld
/// instance-id ledger (via <see cref="IPlayerInventoryState"/>) with the
/// ChatWorld name-keyed stack-size observations (via
/// <see cref="IChatInventoryState"/>), pairing them with a TTL-windowed
/// correlator keyed on <c>(InternalName, Server, Character)</c>.
///
/// <para><b>Three consumer surfaces.</b> Per the React / Query / Bind
/// taxonomy in <c>docs/module-charters.md</c>:
/// <list type="bullet">
///   <item><b>React</b> — <see cref="Bus"/> publishes typed
///   <see cref="InventoryItemAdded"/> / <see cref="InventoryItemRemoved"/> /
///   <see cref="InventoryStackChanged"/> frames.</item>
///   <item><b>Query</b> — <see cref="TryResolve"/> /
///   <see cref="TryGetStackSize"/> answer "what's in inventory now?".</item>
///   <item><b>Bind</b> — <see cref="Items"/> is a live observable collection
///   of <see cref="InventoryItem"/> rows for WPF binding (#729).</item>
/// </list>
/// The pre-#659 union-shaped <c>Subscribe(Action&lt;InventoryEvent&gt;)</c>
/// shim retired in #659; consumers landed at PlayerWorld-direct
/// (Samwise/Legolas/Motherlode), the Bind channel (Palantir), the Tier-2
/// <c>IGiftSignalService</c> (Arwen), or stayed blueprint-only (Saruman).</para>
///
/// <para><b>Why a view, not a service.</b> Pre-#602 <c>IInventoryService</c>
/// consumed both Player.log AND chat directly. That violates the world-sim
/// principle 3 — no service spans both sources. The split puts each source
/// in its own world; cross-source composition lives in this view layer (per
/// principle 4 + §Worked example 1 of <c>docs/world-simulator.md</c>).</para>
/// </summary>
public interface IInventoryView
{
    /// <summary>
    /// The view's typed-frame bus. Subscribe via
    /// <c>Bus.Subscribe&lt;InventoryItemAdded&gt;(...)</c> /
    /// <c>InventoryItemRemoved</c> / <c>InventoryStackChanged</c> for the
    /// canonical post-migration surface.
    /// </summary>
    IWorldEventBus Bus { get; }

    /// <summary>
    /// Bindable live view of the currently-tracked inventory state — the
    /// third read surface alongside the typed event <see cref="Bus"/> (React)
    /// and <see cref="TryResolve"/> / <see cref="TryGetStackSize"/>
    /// (Query). Per-item state changes (<c>StackSize</c>, <c>SizeConfirmed</c>,
    /// <c>IsDeleted</c>) propagate as <c>INotifyPropertyChanged</c> on each
    /// row; add of a new instance id propagates as
    /// <c>NotifyCollectionChangedAction.Add</c>. Removals follow the
    /// <b>soft-delete</b> contract — rows stay in the collection with
    /// <see cref="InventoryItem.IsDeleted"/> = <c>true</c>, matching the
    /// view's existing <c>_map</c> retention (Arwen's gift-attribution path
    /// needs pre-delete state). The "Bind" channel of the three-channel
    /// state-service contract from <c>docs/module-charters.md</c>.
    ///
    /// <para><b>Threading.</b> Mutations fire under the view's internal
    /// <see cref="ItemsSyncRoot"/> on whatever thread originated the
    /// underlying world bus frame (Player.log L1 dispatch or chat dispatch).
    /// WPF consumers binding from a non-dispatcher thread MUST call
    /// <c>BindingOperations.EnableCollectionSynchronization(view.Items,
    /// view.ItemsSyncRoot)</c> once at bind time so XAML notifications
    /// marshal correctly across threads.</para>
    /// </summary>
    IReadOnlyObservableCollection<InventoryItem> Items { get; }

    /// <summary>
    /// Lock the view holds while mutating <see cref="Items"/> + firing
    /// per-row notifications. Passed to
    /// <c>BindingOperations.EnableCollectionSynchronization</c> by WPF
    /// consumers binding from a non-dispatcher thread.
    /// </summary>
    object ItemsSyncRoot { get; }

    /// <summary>
    /// Resolve an instance id to its <c>InternalName</c> via the PlayerWorld
    /// ledger (passthrough of <see cref="IPlayerInventoryState.TryResolve"/>
    /// — retained entries survive deletion).
    /// </summary>
    bool TryResolve(long instanceId, out string internalName);

    /// <summary>
    /// Resolve an instance id to its current stack size, if and only if the
    /// size has been confirmed by an authoritative source (chat correlation,
    /// non-stackable reference data, export seed, <c>ProcessUpdateItemCode</c>,
    /// <c>ProcessRemoveFromStorageVault</c>). The legacy contract — preserved
    /// verbatim for the pre-#602 consumers.
    /// </summary>
    bool TryGetStackSize(long instanceId, out int stackSize);
}
