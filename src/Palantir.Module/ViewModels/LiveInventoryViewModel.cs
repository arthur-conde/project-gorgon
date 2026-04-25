using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Shared.Inventory;
using Mithril.Shared.Reference;

namespace Palantir.ViewModels;

/// <summary>
/// Mirrors <see cref="IInventoryService"/>'s canonical map row-by-row so the
/// developer view can inspect what the simulator currently knows. Replay on
/// subscribe seeds <see cref="Rows"/>; live <c>Added</c>/<c>Deleted</c>/<c>StackChanged</c>
/// events keep it current. Deleted entries are kept in the index (so toggling
/// "show deleted" can reveal them) but hidden from <see cref="View"/> by default.
/// </summary>
public sealed partial class LiveInventoryViewModel : ObservableObject, IDisposable
{
    private readonly IInventoryService _inventory;
    private readonly IReferenceDataService? _refData;
    private readonly Action<Action> _dispatch;
    private readonly Dictionary<long, LiveInventoryRow> _index = new();
    private IDisposable? _subscription;

    public ObservableCollection<LiveInventoryRow> Rows { get; } = new();
    public ICollectionView View { get; }

    [ObservableProperty] private int _liveCount;
    [ObservableProperty] private int _deletedCount;
    [ObservableProperty] private bool _showDeleted;
    [ObservableProperty] private string _queryText = "";

    public LiveInventoryViewModel(IInventoryService inventory, IReferenceDataService? refData = null)
        : this(inventory, refData, dispatch: null)
    { }

    /// <summary>
    /// Test-friendly ctor: callers can inject a synchronous dispatcher so unit
    /// tests don't need an STA Application running.
    /// </summary>
    public LiveInventoryViewModel(
        IInventoryService inventory,
        IReferenceDataService? refData,
        Action<Action>? dispatch)
    {
        _inventory = inventory;
        _refData = refData;
        _dispatch = dispatch ?? DefaultDispatch;

        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is LiveInventoryRow r && (ShowDeleted || !r.IsDeleted);
        View.SortDescriptions.Add(new SortDescription(nameof(LiveInventoryRow.LastUpdated), ListSortDirection.Descending));

        _subscription = _inventory.Subscribe(OnEvent);
    }

    partial void OnShowDeletedChanged(bool value) => _dispatch(() => View.Refresh());

    private void OnEvent(InventoryEvent e) => _dispatch(() => Apply(e));

    private void Apply(InventoryEvent e)
    {
        switch (e.Kind)
        {
            case InventoryEventKind.Added:
                Upsert(e);
                break;
            case InventoryEventKind.Deleted:
                if (_index.TryGetValue(e.InstanceId, out var delRow))
                {
                    delRow.IsDeleted = true;
                    delRow.LastUpdated = e.Timestamp;
                    delRow.StackSize = e.StackSize;
                }
                break;
            case InventoryEventKind.StackChanged:
                if (_index.TryGetValue(e.InstanceId, out var stackRow))
                {
                    stackRow.StackSize = e.StackSize;
                    stackRow.LastUpdated = e.Timestamp;
                }
                break;
        }
        RefreshCounts();
        View.Refresh();
    }

    private void Upsert(InventoryEvent e)
    {
        if (_index.TryGetValue(e.InstanceId, out var row))
        {
            // Re-add of an instance we already know about (rare, but possible if the
            // game emits a duplicate). Treat it as a refresh.
            row.IsDeleted = false;
            row.StackSize = e.StackSize;
            row.LastUpdated = e.Timestamp;
            return;
        }

        var (display, iconId) = ResolveDisplay(e.InternalName);
        row = new LiveInventoryRow
        {
            InstanceId = e.InstanceId,
            InternalName = e.InternalName,
            Name = display,
            IconId = iconId,
            StackSize = e.StackSize,
            IsDeleted = false,
            LastUpdated = e.Timestamp,
        };
        _index[e.InstanceId] = row;
        Rows.Add(row);
    }

    private (string Display, int IconId) ResolveDisplay(string internalName)
    {
        if (_refData is not null && _refData.ItemsByInternalName.TryGetValue(internalName, out var item))
            return (item.Name, item.IconId);
        return (internalName, 0);
    }

    private void RefreshCounts()
    {
        var live = 0;
        var deleted = 0;
        foreach (var r in _index.Values)
        {
            if (r.IsDeleted) deleted++; else live++;
        }
        LiveCount = live;
        DeletedCount = deleted;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private static void DefaultDispatch(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}
