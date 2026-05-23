using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mithril.GameState.Inventory;

/// <summary>
/// Bindable row in <see cref="IInventoryView.Items"/> — one entry per
/// inventory instance the view currently tracks (#729). Mirrors the
/// canonical <c>(InstanceId → InternalName + StackSize + SizeConfirmed)</c>
/// state with WPF-friendly <see cref="INotifyPropertyChanged"/> plumbing so
/// UI consumers bind to per-row state changes directly instead of
/// re-mirroring the view's event stream into a local collection.
///
/// <para><b>Mutation policy.</b> Properties are settable only through the
/// view's internal correlator paths — the type exposes <c>internal</c>
/// setters so external code cannot reach in and re-write state. The view
/// holds <c>_stateLock</c> when calling the setters; <see cref="PropertyChanged"/>
/// fires synchronously on whatever thread owns the lock at the time. WPF
/// consumers binding from a non-dispatcher thread call
/// <c>BindingOperations.EnableCollectionSynchronization</c> with the view's
/// <see cref="IInventoryView.ItemsSyncRoot"/> to marshal cross-thread.</para>
///
/// <para><b>Soft-delete semantic.</b> When the player.log <c>ProcessDeleteItem</c>
/// fires for an instance id, <see cref="IsDeleted"/> flips to <c>true</c> but
/// the row stays in <see cref="IInventoryView.Items"/>. This matches the
/// view's existing <c>_map</c> retention contract (Arwen's gift-attribution
/// path needs the pre-delete name + size via <c>TryResolve</c> /
/// <c>TryGetStackSize</c>) and Palantir's pre-#729 row-greying behaviour
/// (the live-inventory grid hides deleted rows by default but keeps them in
/// the underlying index so "show deleted" can reveal them). See PR #729 body
/// for the rationale.</para>
/// </summary>
public sealed class InventoryItem : INotifyPropertyChanged
{
    internal InventoryItem(long instanceId, string internalName, int stackSize, bool sizeConfirmed)
    {
        InstanceId = instanceId;
        InternalName = internalName;
        _stackSize = stackSize;
        _sizeConfirmed = sizeConfirmed;
    }

    /// <summary>PG's per-instance unique id (from <c>ProcessAddItem</c>). Immutable.</summary>
    public long InstanceId { get; }

    /// <summary>Reference-data <c>InternalName</c> key. Immutable — the
    /// view never re-labels an instance id under a different name.</summary>
    public string InternalName { get; }

    private int _stackSize;

    /// <summary>Current stack size. Updates on chat correlation, player
    /// <c>ProcessUpdateItemCode</c>/<c>ProcessRemoveFromStorageVault</c>,
    /// non-stackable confirmation, and export reconcile.</summary>
    public int StackSize
    {
        get => _stackSize;
        internal set => Set(ref _stackSize, value);
    }

    private bool _sizeConfirmed;

    /// <summary><c>true</c> once an authoritative source (chat correlation,
    /// non-stackable reference data, export seed/reconcile, player stack
    /// update) has spoken for this entry. Flips false → true when chat
    /// correlation arrives after the Add — see
    /// <see cref="IInventoryView.TryGetStackSize"/>.</summary>
    public bool SizeConfirmed
    {
        get => _sizeConfirmed;
        internal set => Set(ref _sizeConfirmed, value);
    }

    private bool _isDeleted;

    /// <summary>Soft-delete marker — set by <see cref="IInventoryView"/>
    /// when the underlying instance id is removed from inventory. The row
    /// stays in <see cref="IInventoryView.Items"/> with this flag flipped
    /// so consumers can grey out / filter deleted entries while preserving
    /// late lookup of pre-delete state (Arwen's gift-attribution path).</summary>
    public bool IsDeleted
    {
        get => _isDeleted;
        internal set => Set(ref _isDeleted, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
