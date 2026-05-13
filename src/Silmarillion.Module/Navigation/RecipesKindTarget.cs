using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> adapter for the Recipes tab.
/// See <see cref="ItemsKindTarget"/> for the rationale on resolving against
/// the tab VM's bound collection instead of refData directly.
/// </summary>
public sealed class RecipesKindTarget : IReferenceKindTarget
{
    private readonly RecipesTabViewModel _vm;

    public RecipesKindTarget(RecipesTabViewModel vm)
    {
        _vm = vm;
    }

    public EntityKind Kind => EntityKind.Recipe;

    public int TabIndex => 1;

    public bool TrySelectByInternalName(string internalName)
    {
        // Resolve against AllRecipes (canonical, matches WPF's ListBox ItemsSource),
        // not against refData. See ItemsKindTarget for context. The pre-existing
        // SelectedRecipe setter also uses ReferenceEquals, which fails post-refresh
        // when references diverge — going through SelectedRow directly avoids it.
        var row = _vm.AllRecipes.FirstOrDefault(r => r.Recipe.InternalName == internalName);
        if (row is null) return false;
        _vm.SelectedRow = row;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new RecipeDetailWindow { DataContext = _vm.DetailViewModel }.Show();
        return true;
    }
}
