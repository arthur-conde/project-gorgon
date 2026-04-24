using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Celebrimbor.ViewModels;

/// <summary>
/// A header + row bucket for the shopping list, grouped by PrimaryTag. Tracks
/// its own completion state so the view can render a progress bar and auto-collapse
/// when every row in the group is craft-ready.
/// </summary>
public sealed partial class IngredientGroupViewModel : ObservableObject
{
    private bool _userExplicitlyExpanded;

    public IngredientGroupViewModel(string name, IEnumerable<IngredientRowViewModel> rows)
    {
        Name = name;
        foreach (var row in rows)
        {
            Rows.Add(row);
            row.PropertyChanged += OnRowChanged;
        }
        Recompute();
    }

    public string Name { get; }
    public ObservableCollection<IngredientRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private int _craftReadyCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private double _progressPct;

    /// <summary>All rows satisfied. Header stays; body auto-collapses unless the user pinned it open.</summary>
    [ObservableProperty]
    private bool _isComplete;

    /// <summary>Drives the rows' visibility. Auto-collapses when the group completes, but the user can toggle it back.</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>False when this group is the only group in its step — the header would be redundant noise.</summary>
    [ObservableProperty]
    private bool _isHeaderVisible = true;

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        _userExplicitlyExpanded = IsExpanded;
    }

    partial void OnIsCompleteChanged(bool value)
    {
        // Auto-collapse on completion unless the user has opted to keep it open.
        if (value && !_userExplicitlyExpanded) IsExpanded = false;
        if (!value) _userExplicitlyExpanded = false; // reset so a future re-completion auto-collapses again.
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IngredientRowViewModel.IsCraftReady)
            || e.PropertyName == nameof(IngredientRowViewModel.Remaining))
        {
            Recompute();
        }
    }

    private void Recompute()
    {
        TotalCount = Rows.Count;
        CraftReadyCount = Rows.Count(r => r.IsCraftReady);
        ProgressPct = TotalCount == 0 ? 0 : 100.0 * CraftReadyCount / TotalCount;
        IsComplete = TotalCount > 0 && CraftReadyCount == TotalCount;
    }

    public void Detach()
    {
        foreach (var row in Rows) row.PropertyChanged -= OnRowChanged;
    }
}
