using System.Collections.ObjectModel;
using Arda.Composition;
using Arda.Wpf;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;

namespace Palantir.ViewModels;

/// <summary>
/// Developer / debug surface over <see cref="IInventoryAccumulatorState"/>. Shows
/// items from the L4 inventory accumulator (which retains soft-deleted entries) via
/// <see cref="InventoryProjection"/> — a WPF-thread-aware projection that keeps an
/// <see cref="ObservableCollection{T}"/> of <see cref="InventoryItemModel"/> in sync
/// with accumulator state changes.
/// </summary>
public sealed partial class LiveInventoryViewModel : ObservableObject, IDisposable
{
    private readonly InventoryProjection _projection;
    private readonly IReferenceDataService? _refData;

    [ObservableProperty] private int _liveCount;
    [ObservableProperty] private int _deletedCount;
    [ObservableProperty] private bool _showDeleted;
    [ObservableProperty] private string _queryText = "";

    public ObservableCollection<InventoryItemModel> Items => _projection.Items;

    public IReferenceDataService? ReferenceData => _refData;

    public LiveInventoryViewModel(
        IInventoryAccumulatorState accumulator,
        IReferenceDataService? refData = null)
        : this(new InventoryProjection(accumulator), refData)
    { }

    /// <summary>
    /// Test-friendly ctor: inject a pre-built projection (avoids needing WPF dispatcher).
    /// </summary>
    internal LiveInventoryViewModel(
        InventoryProjection projection,
        IReferenceDataService? refData)
    {
        _projection = projection;
        _refData = refData;

        _projection.Items.CollectionChanged += (_, _) => UpdateCounts();
        UpdateCounts();
    }

    partial void OnShowDeletedChanged(bool value) => OnPropertyChanged(nameof(ShowDeleted));

    [RelayCommand]
    private void Refresh() => _projection.ForceRefresh();

    private void UpdateCounts()
    {
        var items = _projection.Items;
        var live = 0;
        var deleted = 0;
        foreach (var item in items)
        {
            if (item.IsRemoved) deleted++;
            else live++;
        }
        LiveCount = live;
        DeletedCount = deleted;
    }

    public void Dispose()
    {
        _projection.Dispose();
    }
}
