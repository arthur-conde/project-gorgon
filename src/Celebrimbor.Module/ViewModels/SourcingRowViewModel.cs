using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Planning;

namespace Celebrimbor.ViewModels;

/// <summary>
/// One ingredient of the walker's current phase with a tri-state sourcing
/// toggle (#228 PR-B/B1, frame 03). The mode is persisted on the plan
/// (<see cref="SavedLevelingPlan.Sourcing"/>) and consumed by the next
/// re-plan — toggling here changes future regeneration, not the current
/// predicted crafts. v1 covers item ingredients only; keyword slots are not
/// individually sourced.
/// </summary>
public sealed partial class SourcingRowViewModel : ObservableObject
{
    private readonly Action<string, SourcingMode> _onModeChanged;

    public SourcingRowViewModel(
        string itemInternalName,
        string displayName,
        int need,
        SourcingMode mode,
        Action<string, SourcingMode> onModeChanged)
    {
        ItemInternalName = itemInternalName;
        DisplayName = displayName;
        Need = need;
        _mode = mode;
        _onModeChanged = onModeChanged;
    }

    public string ItemInternalName { get; }
    public string DisplayName { get; }
    public int Need { get; }

    [ObservableProperty]
    private SourcingMode _mode;

    public bool IsCraft => Mode == SourcingMode.Craft;
    public bool IsSupply => Mode == SourcingMode.SupplyExternally;
    public bool IsIgnore => Mode == SourcingMode.Ignore;

    partial void OnModeChanged(SourcingMode value)
    {
        OnPropertyChanged(nameof(IsCraft));
        OnPropertyChanged(nameof(IsSupply));
        OnPropertyChanged(nameof(IsIgnore));
        _onModeChanged(ItemInternalName, value);
    }

    [RelayCommand]
    private void SetMode(SourcingMode mode) => Mode = mode;
}
