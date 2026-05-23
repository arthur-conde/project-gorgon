using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Mithril.GameState.Inventory;
using Mithril.Shared.Collections;
using Mithril.WorldSim;

namespace Arwen.Tests;

/// <summary>
/// Tests seed the map via <see cref="Add"/> to mirror what the real
/// <see cref="InventoryView"/> would learn from <c>ProcessAddItem</c> and the
/// chat-correlation / <c>UpdateItemCode</c> paths. Implements the Query slice
/// of <see cref="IInventoryView"/> that <c>CalibrationService</c> consumes
/// (<c>TryResolve</c> / <c>TryGetStackSize</c>); the React/Bind members
/// (<see cref="Bus"/> / <see cref="Items"/> / <see cref="ItemsSyncRoot"/>) are
/// stubbed because Arwen does not subscribe to the bus or bind to the items
/// collection.
/// </summary>
internal sealed class FakeInventory : IInventoryView
{
    private readonly Dictionary<long, (string Name, int StackSize)> _map = new();
    public void Add(long id, string name, int stackSize = 1) => _map[id] = (name, stackSize);

    public IWorldEventBus Bus => throw new NotSupportedException("Arwen's CalibrationService consumes Query-only.");
    public IReadOnlyObservableCollection<InventoryItem> Items { get; } = new ObservableInventoryItemsStub();
    public object ItemsSyncRoot { get; } = new();

    public bool TryResolve(long instanceId, out string internalName)
    {
        if (_map.TryGetValue(instanceId, out var entry)) { internalName = entry.Name; return true; }
        internalName = "";
        return false;
    }
    public bool TryGetStackSize(long instanceId, out int stackSize)
    {
        if (_map.TryGetValue(instanceId, out var entry) && entry.StackSize > 0)
        {
            stackSize = entry.StackSize;
            return true;
        }
        stackSize = 0;
        return false;
    }

    /// <summary>
    /// Empty bindable-items stub — mirrors the shape Palantir's
    /// <c>FakeInventoryView</c> uses, scoped down to the read-only members
    /// since Arwen never seeds into it.
    /// </summary>
    private sealed class ObservableInventoryItemsStub : IReadOnlyObservableCollection<InventoryItem>, IList
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
        object? IList.this[int index] { get => _inner[index]; set => throw new NotSupportedException(); }
        bool IList.IsReadOnly => true;
        bool IList.IsFixedSize => false;
        bool ICollection.IsSynchronized => ((ICollection)_inner).IsSynchronized;
        object ICollection.SyncRoot => ((ICollection)_inner).SyncRoot;
        int IList.Add(object? value) => throw new NotSupportedException();
        void IList.Clear() => throw new NotSupportedException();
        bool IList.Contains(object? value) => ((IList)_inner).Contains(value);
        int IList.IndexOf(object? value) => ((IList)_inner).IndexOf(value);
        void IList.Insert(int index, object? value) => throw new NotSupportedException();
        void IList.Remove(object? value) => throw new NotSupportedException();
        void IList.RemoveAt(int index) => throw new NotSupportedException();
        void ICollection.CopyTo(Array array, int index) => ((ICollection)_inner).CopyTo(array, index);
    }
}
