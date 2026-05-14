using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> for <see cref="EntityKind.RecipeIngredientItem"/>.
/// Flips to the Recipes tab and pre-populates <see cref="RecipesTabViewModel.QueryText"/>
/// with <c>Ingredients CONTAINS "&lt;itemInternalName&gt;"</c>. Mirror of
/// <see cref="RecipeIngredientKeywordKindTarget"/> for the item-pivot direction — powers
/// the item-detail "Used in" overflow pill (<c>+N more →</c>) when the per-recipe chip
/// count exceeds <c>SilmarillionSettings.UsedInChipCap</c>.
/// </summary>
public sealed class RecipeIngredientItemKindTarget : IReferenceKindTarget
{
    private readonly RecipesTabViewModel _vm;
    private readonly IDiagnosticsSink? _diag;

    public RecipeIngredientItemKindTarget(RecipesTabViewModel vm, IDiagnosticsSink? diag = null)
    {
        _vm = vm;
        _diag = diag;
    }

    public EntityKind Kind => EntityKind.RecipeIngredientItem;

    public int TabIndex => 1;

    public bool TrySelectByInternalName(string internalName)
    {
        var query = $"Ingredients CONTAINS \"{internalName}\"";
        _diag?.Info("Silmarillion.Nav", $"RecipeIngredientItem.TrySelect '{internalName}' → setting QueryText.");
        _vm.SelectedRow = null;
        _vm.QueryText = query;
        return true;
    }

    public bool TryOpenInWindow() => false;
}
