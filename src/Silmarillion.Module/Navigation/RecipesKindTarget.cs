using Microsoft.Extensions.Logging;
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
    private readonly ILogger? _logger;

    public RecipesKindTarget(RecipesTabViewModel vm, ILogger? logger = null)
    {
        _vm = vm;
        _logger = logger;
    }

    public EntityKind Kind => EntityKind.Recipe;

    public int TabIndex => 1;

    public bool TrySelectByInternalName(string internalName)
    {
        // Resolve against AllRecipes (canonical, matches WPF's ListBox ItemsSource),
        // not against refData. See ItemsKindTarget for context.
        var row = _vm.AllRecipes.FirstOrDefault(r => r.Recipe.InternalName == internalName);
        if (row is null)
        {
            _logger?.LogTrace("Recipes.TrySelect '{InternalName}' → not found (AllRecipes={AllRecipes}).", internalName, _vm.AllRecipes.Count);
            return false;
        }
        _logger?.LogTrace("Recipes.TrySelect '{InternalName}' → found, selecting.", internalName);
        // Clear any residual filter so the target row isn't filtered out of the visible
        // ListBox. See ItemsKindTarget for the symptom — particularly relevant now that
        // the item-detail "Used as" keyword chip leaves an IngredientKeywords filter in
        // the box; a subsequent recipe-link navigation needs to land cleanly.
        _vm.QueryText = "";
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
