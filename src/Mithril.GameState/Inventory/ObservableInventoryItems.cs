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
/// </summary>
internal sealed class ObservableInventoryItems : IReadOnlyObservableCollection<InventoryItem>
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
}
