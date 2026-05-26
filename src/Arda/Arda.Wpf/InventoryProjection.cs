using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Arda.Composition;

namespace Arda.Wpf;

/// <summary>
/// Thin WPF projection that watches <see cref="IInventoryAccumulatorState"/> and
/// projects mutations into an <see cref="ObservableCollection{T}"/> on the UI thread.
/// </summary>
public sealed class InventoryProjection : IDisposable
{
    private readonly IInventoryAccumulatorState _state;
    private readonly Dictionary<long, InventoryItemModel> _index = new();
    private Dispatcher? _dispatcher;

    public ObservableCollection<InventoryItemModel> Items { get; } = [];

    public InventoryProjection(IInventoryAccumulatorState state)
    {
        _state = state;
        _dispatcher = Application.Current?.Dispatcher;
        _state.StateChanged += OnStateChanged;
        Refresh();
    }

    private void OnStateChanged()
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
            Refresh();
        else
            _dispatcher.InvokeAsync(Refresh, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        var source = _state.Items;

        foreach (var (id, item) in source)
        {
            if (_index.TryGetValue(id, out var model))
            {
                model.InternalName = item.InternalName;
                model.DisplayName = item.DisplayName;
                model.StackSize = item.StackSize;
                model.TypeId = item.TypeId;
                model.IsRemoved = item.IsRemoved;
                model.RemovedAt = item.RemovedAt;
                model.LastUpdatedAt = item.LastUpdatedAt;
            }
            else
            {
                model = new InventoryItemModel(id)
                {
                    InternalName = item.InternalName,
                    DisplayName = item.DisplayName,
                    StackSize = item.StackSize,
                    TypeId = item.TypeId,
                    IsRemoved = item.IsRemoved,
                    RemovedAt = item.RemovedAt,
                    FirstSeenAt = item.FirstSeenAt,
                    LastUpdatedAt = item.LastUpdatedAt
                };
                _index[id] = model;
                Items.Add(model);
            }
        }

        var toRemove = new List<long>();
        foreach (var (id, _) in _index)
        {
            if (!source.ContainsKey(id))
                toRemove.Add(id);
        }
        foreach (var id in toRemove)
        {
            if (_index.Remove(id, out var model))
                Items.Remove(model);
        }
    }

    public void Dispose()
    {
        _state.StateChanged -= OnStateChanged;
    }
}
