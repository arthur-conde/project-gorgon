using System.Collections.ObjectModel;
using Arda.Dispatch;
using Arda.World.Player;
using Arda.World.Player.Events;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;

namespace Palantir.ViewModels;

/// <summary>
/// Developer / debug surface over <see cref="IInventoryState.Items"/>. Shows
/// a snapshot of the player's bag inventory, refreshed via domain events
/// (<see cref="InventoryItemAdded"/>, <see cref="InventoryItemRemoved"/>,
/// <see cref="InventoryItemUpdated"/>).
///
/// <para>Unlike the previous <c>IInventoryView</c>-backed implementation that
/// relied on an <c>IReadOnlyObservableCollection</c> with
/// <c>EnableCollectionSynchronization</c>, this version maintains its own
/// <see cref="ObservableCollection{T}"/> populated from the state dictionary
/// and refreshed on every inventory event. The inventory is small enough that
/// a full refresh is cheap and keeps the code simple for this debug surface.</para>
/// </summary>
public sealed partial class LiveInventoryViewModel : ObservableObject, IDisposable
{
    private readonly IInventoryState _inventory;
    private readonly IReferenceDataService? _refData;
    private readonly Action<Action> _dispatch;

    private IDisposable? _addedSub;
    private IDisposable? _removedSub;
    private IDisposable? _updatedSub;

    [ObservableProperty] private int _liveCount;
    [ObservableProperty] private string _queryText = "";

    /// <summary>
    /// The bindable collection backing the grid. Rebuilt from
    /// <see cref="IInventoryState.Items"/> on every inventory event.
    /// </summary>
    public ObservableCollection<InventoryRow> Items { get; } = [];

    /// <summary>Reference-data accessor surfaced for the icon/name converter
    /// referenced by the XAML cells — null in tests / when bundled data hasn't
    /// loaded.</summary>
    public IReferenceDataService? ReferenceData => _refData;

    public LiveInventoryViewModel(
        IInventoryState inventory,
        IDomainEventSubscriber bus,
        IReferenceDataService? refData = null)
        : this(inventory, bus, refData, dispatch: null)
    { }

    /// <summary>
    /// Test-friendly ctor: inject a synchronous dispatcher so unit tests
    /// don't need an STA Application running.
    /// </summary>
    public LiveInventoryViewModel(
        IInventoryState inventory,
        IDomainEventSubscriber bus,
        IReferenceDataService? refData,
        Action<Action>? dispatch)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _refData = refData;
        _dispatch = dispatch ?? DefaultDispatch;

        RefreshFromState();

        _addedSub = bus.Subscribe<InventoryItemAdded>(_ => _dispatch(RefreshFromState));
        _removedSub = bus.Subscribe<InventoryItemRemoved>(_ => _dispatch(RefreshFromState));
        _updatedSub = bus.Subscribe<InventoryItemUpdated>(_ => _dispatch(RefreshFromState));
    }

    [RelayCommand]
    private void Refresh() => _dispatch(RefreshFromState);

    private void RefreshFromState()
    {
        Items.Clear();
        foreach (var (id, entry) in _inventory.Items)
            Items.Add(new InventoryRow(id, entry.InternalName, entry.StackSize));
        LiveCount = Items.Count;
    }

    public void Dispose()
    {
        _addedSub?.Dispose();
        _addedSub = null;
        _removedSub?.Dispose();
        _removedSub = null;
        _updatedSub?.Dispose();
        _updatedSub = null;
    }

    private static void DefaultDispatch(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}

/// <summary>
/// Presentation row for the inventory debug grid.
/// </summary>
public sealed record InventoryRow(long InstanceId, string InternalName, int StackSize);
