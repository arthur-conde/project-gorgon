using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Celebrimbor.ViewModels;

public sealed partial class CelebrimborShellViewModel : ObservableObject
{
    private CelebrimborViewMode _lastCraftingView = CelebrimborViewMode.Picker;

    public CelebrimborShellViewModel(
        RecipePickerViewModel picker, ShoppingListViewModel shopping, PlansViewModel plans)
    {
        Picker = picker;
        Shopping = shopping;
        Plans = plans;

        Picker.FinalizeRequested += (_, _) =>
        {
            if (!Picker.HasCraftList) return;
            Shopping.Rebuild();
            CurrentView = CelebrimborViewMode.Shopping;
        };
        Picker.CraftListChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsShoppingAvailable));
            GoToShoppingCommand.NotifyCanExecuteChanged();
            if (CurrentView == CelebrimborViewMode.Shopping) Shopping.Rebuild();
        };
        Shopping.BackRequested += (_, _) => CurrentView = CelebrimborViewMode.Picker;
    }

    public RecipePickerViewModel Picker { get; }
    public ShoppingListViewModel Shopping { get; }
    public PlansViewModel Plans { get; }

    [ObservableProperty]
    private CelebrimborViewMode _currentView = CelebrimborViewMode.Picker;

    public bool IsShoppingAvailable => Picker.HasCraftList;

    /// <summary>The crafting wizard (Pick → Shop) is one peer area; Plans is the other.</summary>
    public bool IsCraftingArea => CurrentView is CelebrimborViewMode.Picker or CelebrimborViewMode.Shopping;
    public bool IsPlansArea => CurrentView is CelebrimborViewMode.Plans;

    partial void OnCurrentViewChanged(CelebrimborViewMode value)
    {
        if (value is CelebrimborViewMode.Picker or CelebrimborViewMode.Shopping)
            _lastCraftingView = value;
        OnPropertyChanged(nameof(IsCraftingArea));
        OnPropertyChanged(nameof(IsPlansArea));
    }

    [RelayCommand]
    private void GoToPicker() => CurrentView = CelebrimborViewMode.Picker;

    [RelayCommand(CanExecute = nameof(IsShoppingAvailable))]
    private void GoToShopping()
    {
        Shopping.Rebuild();
        CurrentView = CelebrimborViewMode.Shopping;
    }

    /// <summary>Header pivot → Plans area (independent of the crafting wizard).</summary>
    [RelayCommand]
    private void GoToPlans()
    {
        Plans.Reload();
        CurrentView = CelebrimborViewMode.Plans;
    }

    /// <summary>Header pivot → back to the crafting wizard at its last sub-step.</summary>
    [RelayCommand]
    private void GoToCrafting() => CurrentView = _lastCraftingView;
}
