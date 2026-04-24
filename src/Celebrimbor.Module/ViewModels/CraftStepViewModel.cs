using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Celebrimbor.ViewModels;

/// <summary>
/// An outer grouping in the shopping list — a "crafting step" derived from the
/// aggregator's dependency depth. Step 1 is always raw materials; the deepest
/// step is the last craft before the final target recipes. Each step contains
/// PrimaryTag groups so the keyword bucketing still works within a step.
/// </summary>
public sealed partial class CraftStepViewModel : ObservableObject
{
    private bool _userExplicitlyExpanded;

    public CraftStepViewModel(int number, string label, IEnumerable<IngredientGroupViewModel> groups)
    {
        Number = number;
        Label = label;
        foreach (var g in groups)
        {
            Groups.Add(g);
            g.PropertyChanged += OnGroupChanged;
        }
        Recompute();
    }

    public int Number { get; }
    public string Label { get; }
    public ObservableCollection<IngredientGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private int _craftReadyCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private double _progressPct;

    [ObservableProperty]
    private bool _isComplete;

    [ObservableProperty]
    private bool _isExpanded = true;

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        _userExplicitlyExpanded = IsExpanded;
    }

    partial void OnIsCompleteChanged(bool value)
    {
        if (value && !_userExplicitlyExpanded) IsExpanded = false;
        if (!value) _userExplicitlyExpanded = false;
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IngredientGroupViewModel.CraftReadyCount)
            or nameof(IngredientGroupViewModel.TotalCount))
        {
            Recompute();
        }
    }

    private void Recompute()
    {
        TotalCount = Groups.Sum(g => g.TotalCount);
        CraftReadyCount = Groups.Sum(g => g.CraftReadyCount);
        ProgressPct = TotalCount == 0 ? 0 : 100.0 * CraftReadyCount / TotalCount;
        IsComplete = TotalCount > 0 && CraftReadyCount == TotalCount;
    }

    public void Detach()
    {
        foreach (var g in Groups)
        {
            g.PropertyChanged -= OnGroupChanged;
            g.Detach();
        }
    }
}
