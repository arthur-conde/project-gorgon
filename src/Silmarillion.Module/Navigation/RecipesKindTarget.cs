using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary><see cref="IReferenceKindTarget"/> adapter for the Recipes tab.
/// See <see cref="ItemsKindTarget"/>.</summary>
public sealed class RecipesKindTarget : IReferenceKindTarget
{
    private readonly RecipesTabViewModel _vm;
    private readonly IReferenceDataService _refData;

    public RecipesKindTarget(RecipesTabViewModel vm, IReferenceDataService refData)
    {
        _vm = vm;
        _refData = refData;
    }

    public EntityKind Kind => EntityKind.Recipe;

    public int TabIndex => 1;

    public bool TrySelectByInternalName(string internalName)
    {
        if (!_refData.RecipesByInternalName.TryGetValue(internalName, out var recipe))
            return false;
        _vm.SelectedRecipe = recipe;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new RecipeDetailWindow { DataContext = _vm.DetailViewModel }.Show();
        return true;
    }
}
