using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Pippin.Domain;
using Pippin.ViewModels;

namespace Pippin.Sharing;

/// <summary>
/// View model for the read-only "shared progress" surface — populated from a
/// <see cref="PippinSharePayload"/> received via a <c>mithril://pippin/…</c> deep link.
/// Joins the sender's <c>InternalName</c> keys against the recipient's local
/// <see cref="FoodCatalog"/> for display metadata, so a sender on an older CDN
/// snapshot still renders correctly on a recipient who's caught up.
/// </summary>
public sealed partial class SharedProgressViewModel : ObservableObject
{
    private readonly FoodCatalog _catalog;
    private readonly ICollectionView _foodsView;

    public SharedProgressViewModel(PippinSharePayload payload, FoodCatalog catalog)
    {
        _catalog = catalog;
        Foods = new ObservableCollection<FoodItemViewModel>();
        _foodsView = CollectionViewSource.GetDefaultView(Foods);
        _foodsView.Filter = PassesComboFilters;

        Title = string.IsNullOrWhiteSpace(payload.CharacterName)
            ? "Shared progress"
            : $"Shared progress · {payload.CharacterName}";

        SyncedLabel = payload.LastReportTime is { } t
            ? $"Synced {t.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)}"
            : "Sync time not available";

        Rebuild(payload);
    }

    public string Title { get; }
    public string SyncedLabel { get; }

    [ObservableProperty] private string _foodTypeFilter = "All";
    [ObservableProperty] private string _eatenFilter = "All";

    [ObservableProperty] private int _eatenCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private double _completionPercent;

    public ObservableCollection<FoodItemViewModel> Foods { get; }

    partial void OnFoodTypeFilterChanged(string value) => _foodsView.Refresh();
    partial void OnEatenFilterChanged(string value) => _foodsView.Refresh();

    private void Rebuild(PippinSharePayload payload)
    {
        var unknown = payload.UnknownByName ?? new Dictionary<string, int>();
        var list = new List<FoodItemViewModel>(_catalog.TotalCount + unknown.Count);

        // Shared payloads don't include the sender's Gourmand level, and "locked" is a
        // viewer-relative state (what *I* can't eat yet), not a sender-relative one.
        // Pass int.MaxValue so nothing on a remote progress view renders as locked.
        foreach (var food in _catalog.ByInternalName.Values)
        {
            var isEaten = payload.EatenFoodsByInternalName.TryGetValue(food.InternalName, out var count);
            list.Add(new FoodItemViewModel(food, isEaten, isEaten ? count : 0, int.MaxValue));
        }

        // Sender had foods we don't recognize — show them in the same orphan section
        // the live view uses for unknowns.
        foreach (var (name, count) in unknown)
            list.Add(new FoodItemViewModel(name, count));

        // Sender is on a newer CDN than us: their InternalName isn't in our catalog.
        // Surface it as a best-effort orphan keyed by InternalName.
        foreach (var (internalName, count) in payload.EatenFoodsByInternalName)
        {
            if (_catalog.TryGetByInternalName(internalName, out _)) continue;
            list.Add(new FoodItemViewModel(internalName, count));
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        Foods.Clear();
        foreach (var vm in list) Foods.Add(vm);
        _foodsView.Refresh();

        EatenCount = payload.EatenFoodsByInternalName.Count + unknown.Count;
        TotalCount = _catalog.TotalCount;
        CompletionPercent = TotalCount > 0 ? Math.Round(100.0 * EatenCount / TotalCount, 1) : 0;
    }

    private bool PassesComboFilters(object obj)
    {
        if (obj is not FoodItemViewModel vm) return false;

        if (FoodTypeFilter != "All" &&
            !vm.FoodType.Equals(FoodTypeFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (EatenFilter == "Eaten" && !vm.IsEaten) return false;
        if (EatenFilter == "Uneaten" && vm.IsEaten) return false;

        return true;
    }
}
