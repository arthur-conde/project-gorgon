using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.GameState.Inventory;
using Mithril.Shared.Collections;
using Mithril.Shared.Reference;

namespace Palantir.ViewModels;

/// <summary>
/// Developer / debug surface over <see cref="IInventoryView.Items"/> — the
/// canonical bindable inventory state shipped in #729. The view-model exposes
/// a default <see cref="ICollectionView"/> with the existing "hide deleted"
/// filter + the row counts the Palantir tab strip displays.
///
/// <para><b>What this used to do.</b> Pre-#726 Palantir subscribed to the
/// legacy union-shaped <c>IInventoryService.Subscribe(Action&lt;InventoryEvent&gt;)</c>
/// shim and re-mirrored every Added / Deleted / StackChanged into a local
/// <c>ObservableCollection&lt;LiveInventoryRow&gt;</c>. With #729's bindable
/// collection surface the re-mirror is redundant: the view's collection IS
/// the state Palantir wants to display, and per-row state changes propagate
/// via <see cref="InventoryItem"/>'s <c>INotifyPropertyChanged</c> directly to
/// XAML.</para>
///
/// <para><b>Counts + filter.</b> <see cref="LiveCount"/> /
/// <see cref="DeletedCount"/> are derived state — recomputed whenever the
/// bound collection adds a row or an existing row's
/// <see cref="InventoryItem.IsDeleted"/> flips. The default
/// <see cref="System.Windows.Data.CollectionView"/> hides
/// <c>IsDeleted=true</c> entries unless <see cref="ShowDeleted"/> is set; the
/// filter is refreshed when the toggle or any row's <c>IsDeleted</c>
/// changes.</para>
///
/// <para><b>Threading.</b> <see cref="IInventoryView.Items"/> mutates on the
/// world's dispatch thread (Player.log L1 / chat L1), not the WPF dispatcher.
/// The ctor calls <see cref="BindingOperations.EnableCollectionSynchronization"/>
/// on the WPF dispatcher with the view's
/// <see cref="IInventoryView.ItemsSyncRoot"/> so XAML notifications marshal
/// correctly. Tests inject a synchronous dispatcher and skip the registration
/// (no <c>Application.Current</c> to call it on).</para>
/// </summary>
public sealed partial class LiveInventoryViewModel : ObservableObject, IDisposable
{
    private readonly IInventoryView _view;
    private readonly IReferenceDataService? _refData;
    private readonly Action<Action> _dispatch;

    [ObservableProperty] private int _liveCount;
    [ObservableProperty] private int _deletedCount;
    [ObservableProperty] private bool _showDeleted;
    [ObservableProperty] private string _queryText = "";

    /// <summary>
    /// The bindable collection backing the grid — exposed for XAML
    /// resource lookups (e.g. CollectionViewSource source resolution) and
    /// reference-data resolution in cell converters.
    /// </summary>
    public IReadOnlyObservableCollection<InventoryItem> Items => _view.Items;

    /// <summary>Reference-data accessor surfaced for the icon/name converter
    /// referenced by the XAML cells — null in tests / when bundled data hasn't
    /// loaded.</summary>
    public IReferenceDataService? ReferenceData => _refData;

    /// <summary>Default filtered view bound to the grid. Hides
    /// <see cref="InventoryItem.IsDeleted"/>=true rows unless
    /// <see cref="ShowDeleted"/> is enabled, and sorts by
    /// <see cref="InventoryItem.InternalName"/> so the list is stable across
    /// re-runs (the pre-#726 default was last-updated DESC, which the view's
    /// surface no longer tracks).</summary>
    public ICollectionView View { get; }

    public LiveInventoryViewModel(IInventoryView view, IReferenceDataService? refData = null)
        : this(view, refData, dispatch: null)
    { }

    /// <summary>
    /// Test-friendly ctor: callers can inject a synchronous dispatcher so unit
    /// tests don't need an STA Application running. When <paramref name="dispatch"/>
    /// is non-null we treat that as the "no WPF dispatcher available" signal
    /// and skip <see cref="BindingOperations.EnableCollectionSynchronization"/>.
    /// </summary>
    public LiveInventoryViewModel(
        IInventoryView view,
        IReferenceDataService? refData,
        Action<Action>? dispatch)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _refData = refData;
        _dispatch = dispatch ?? DefaultDispatch;

        // Register collection synchronization on the WPF dispatcher so XAML
        // observes thread-safe enumeration. Skipped in tests (no Application).
        if (dispatch is null && System.Windows.Application.Current is { } app)
        {
            // Must run on the dispatcher thread that hosts the binding.
            app.Dispatcher.Invoke(() =>
                BindingOperations.EnableCollectionSynchronization(_view.Items, _view.ItemsSyncRoot));
        }

        View = CollectionViewSource.GetDefaultView(_view.Items);
        View.Filter = o => o is InventoryItem r && (ShowDeleted || !r.IsDeleted);
        View.SortDescriptions.Add(new SortDescription(nameof(InventoryItem.InternalName), ListSortDirection.Ascending));

        // Hook collection + per-row INPC so counts / filter stay current.
        _view.Items.CollectionChanged += OnItemsCollectionChanged;
        foreach (var item in _view.Items)
            item.PropertyChanged += OnItemPropertyChanged;
        RefreshCounts();
    }

    partial void OnShowDeletedChanged(bool value) => _dispatch(() => View.Refresh());

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (InventoryItem item in e.NewItems)
                item.PropertyChanged += OnItemPropertyChanged;
        if (e.OldItems is not null)
            foreach (InventoryItem item in e.OldItems)
                item.PropertyChanged -= OnItemPropertyChanged;

        _dispatch(RefreshCounts);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InventoryItem.IsDeleted))
            _dispatch(() => { RefreshCounts(); View.Refresh(); });
    }

    private void RefreshCounts()
    {
        var live = 0;
        var deleted = 0;
        foreach (var r in _view.Items)
        {
            if (r.IsDeleted) deleted++; else live++;
        }
        LiveCount = live;
        DeletedCount = deleted;
    }

    public void Dispose()
    {
        _view.Items.CollectionChanged -= OnItemsCollectionChanged;
        foreach (var item in _view.Items)
            item.PropertyChanged -= OnItemPropertyChanged;
    }

    private static void DefaultDispatch(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}
