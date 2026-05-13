using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;
using Silmarillion.Views;

namespace Silmarillion.ViewModels;

/// <summary>
/// Top-level view-model for the Silmarillion reference-data browser. Hosts the
/// per-tab view-models and the navigation commands surfaced in the header chrome
/// (Back / Forward / Open-in-window). Subscribes to <see cref="IReferenceNavigator.Navigated"/>
/// to drive automatic tab-switching when an entity of a different kind is opened
/// (e.g. clicking a Recipe cross-link from an Item detail).
/// </summary>
public sealed partial class SilmarillionViewModel : ObservableObject
{
    private readonly IReferenceNavigator _navigator;
    private readonly IReferenceDataService _refData;

    public SilmarillionViewModel(
        ItemsTabViewModel items,
        RecipesTabViewModel recipes,
        IReferenceNavigator navigator,
        IReferenceDataService refData)
    {
        Items = items;
        Recipes = recipes;
        _navigator = navigator;
        _refData = refData;

        BackCommand = new RelayCommand(() => _navigator.Back(), () => _navigator.CanGoBack);
        ForwardCommand = new RelayCommand(() => _navigator.Forward(), () => _navigator.CanGoForward);

        _navigator.Navigated += OnNavigated;
    }

    public ItemsTabViewModel Items { get; }
    public RecipesTabViewModel Recipes { get; }

    /// <summary>0 = Items, 1 = Recipes. Two-way bound to the TabControl in the view.</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    public IRelayCommand BackCommand { get; }
    public IRelayCommand ForwardCommand { get; }

    [RelayCommand(CanExecute = nameof(CanOpenInWindow))]
    private void OpenInWindow()
    {
        switch (_navigator.Current?.Kind)
        {
            case EntityKind.Item when Items.DetailViewModel is not null:
                new Mithril.Shared.Wpf.ItemDetailWindow(Items.DetailViewModel).Show();
                break;
            case EntityKind.Recipe when Recipes.DetailViewModel is not null:
                new RecipeDetailWindow { DataContext = Recipes.DetailViewModel }.Show();
                break;
        }
    }

    private bool CanOpenInWindow() => _navigator.Current is not null;

    private void OnNavigated(object? sender, NavigatedEventArgs e)
    {
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        OpenInWindowCommand.NotifyCanExecuteChanged();

        if (e.Current is null) return;

        switch (e.Current.Kind)
        {
            case EntityKind.Item:
                if (_refData.ItemsByInternalName.TryGetValue(e.Current.InternalName, out var item))
                {
                    SelectedTabIndex = 0;
                    Items.SelectedItem = item;
                }
                break;
            case EntityKind.Recipe:
                if (_refData.RecipesByInternalName.TryGetValue(e.Current.InternalName, out var recipe))
                {
                    SelectedTabIndex = 1;
                    Recipes.SelectedRecipe = recipe;
                }
                break;
        }
    }
}

