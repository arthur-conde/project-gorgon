using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Mithril.Shared.Collections;

namespace Mithril.GameState.Inventory;

/// <summary>
/// Wraps an <see cref="ObservableCollection{T}"/> as the
/// <see cref="IReadOnlyObservableCollection{T}"/> surface backing
/// <see cref="IInventoryView.Items"/>. Mutations are restricted to internal
/// callers (<see cref="InventoryView"/>'s correlator paths) — external code
/// gets the read-only interface only.
///
/// <para>The underlying <see cref="ObservableCollection{T}"/> is not
/// thread-safe; <see cref="InventoryView"/> holds <c>_stateLock</c> during
/// every mutation, and WPF consumers binding from a non-dispatcher thread
/// call <c>BindingOperations.EnableCollectionSynchronization</c> with the
/// same lock (exposed via <see cref="IInventoryView.ItemsSyncRoot"/>).</para>
///
/// <para><b>Non-generic <see cref="IList"/> surface (#741).</b> Implemented
/// via explicit-interface methods so the public API typed as
/// <see cref="IReadOnlyObservableCollection{T}"/> is unchanged — mutators are
/// only reachable through an explicit cast to <see cref="IList"/> and throw
/// <see cref="NotSupportedException"/>. WPF's <c>CollectionViewSource</c>
/// reflects on the runtime type; the presence of <see cref="IList"/> is what
/// lets <c>GetDefaultView</c> return a <c>ListCollectionView</c> (supporting
/// sort, filter, group) rather than falling back to
/// <c>EnumerableCollectionView</c>.</para>
/// </summary>
internal sealed class ObservableInventoryItems : IReadOnlyObservableCollection<InventoryItem>, IList
{
    private readonly ObservableCollection<InventoryItem> _inner = new();

    public InventoryItem this[int index] => _inner[index];

    public int Count => _inner.Count;

    public IEnumerator<InventoryItem> GetEnumerator() => _inner.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_inner).GetEnumerator();

    public event NotifyCollectionChangedEventHandler? CollectionChanged
    {
        add => _inner.CollectionChanged += value;
        remove => _inner.CollectionChanged -= value;
    }

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add => ((INotifyPropertyChanged)_inner).PropertyChanged += value;
        remove => ((INotifyPropertyChanged)_inner).PropertyChanged -= value;
    }

    internal void AddItem(InventoryItem item) => _inner.Add(item);

    internal bool RemoveItem(InventoryItem item) => _inner.Remove(item);

    // ── IList / ICollection (non-generic) ──────────────────────────────
    //
    // Read-only members delegate to the inner ObservableCollection<T>
    // (which already implements IList via its Collection<T> base). Mutators
    // throw — the privileged entry points are AddItem/RemoveItem above.
    // Explicit-interface implementations so the public surface typed as
    // IReadOnlyObservableCollection<InventoryItem> is unaffected; see #741.

    private const string ReadOnlyMessage =
        "ObservableInventoryItems is a read-only view; mutations occur via the internal correlator paths in InventoryView.";

    object? IList.this[int index]
    {
        get => _inner[index];
        set => throw new NotSupportedException(ReadOnlyMessage);
    }

    bool IList.IsReadOnly => true;

    // Size changes via the privileged AddItem/RemoveItem paths — not fixed.
    bool IList.IsFixedSize => false;

    bool ICollection.IsSynchronized => ((ICollection)_inner).IsSynchronized;

    object ICollection.SyncRoot => ((ICollection)_inner).SyncRoot;

    int IList.Add(object? value) => throw new NotSupportedException(ReadOnlyMessage);

    void IList.Clear() => throw new NotSupportedException(ReadOnlyMessage);

    bool IList.Contains(object? value) => ((IList)_inner).Contains(value);

    int IList.IndexOf(object? value) => ((IList)_inner).IndexOf(value);

    void IList.Insert(int index, object? value) => throw new NotSupportedException(ReadOnlyMessage);

    void IList.Remove(object? value) => throw new NotSupportedException(ReadOnlyMessage);

    void IList.RemoveAt(int index) => throw new NotSupportedException(ReadOnlyMessage);

    void ICollection.CopyTo(Array array, int index) => ((ICollection)_inner).CopyTo(array, index);
}
