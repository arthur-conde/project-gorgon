using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Celebrimbor.ViewModels;

public sealed partial class CelebrimborShellViewModel : ObservableObject
{
    public CelebrimborShellViewModel(RecipePickerViewModel picker, ShoppingListViewModel shopping)
    {
        Picker = picker;
        Shopping = shopping;

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

    [ObservableProperty]
    private CelebrimborViewMode _currentView = CelebrimborViewMode.Picker;

    public bool IsShoppingAvailable => Picker.HasCraftList;

    [RelayCommand]
    private void GoToPicker() => CurrentView = CelebrimborViewMode.Picker;

    [RelayCommand(CanExecute = nameof(IsShoppingAvailable))]
    private void GoToShopping()
    {
        Shopping.Rebuild();
        CurrentView = CelebrimborViewMode.Shopping;
    }
}
