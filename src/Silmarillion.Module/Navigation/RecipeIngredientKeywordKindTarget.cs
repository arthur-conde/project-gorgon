using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> for <see cref="EntityKind.RecipeIngredientKeyword"/>.
/// Flips to the Recipes tab and pre-populates <see cref="RecipesTabViewModel.QueryText"/>
/// with <c>IngredientKeywords CONTAINS "&lt;keyword&gt;"</c>, leveraging PR #261's
/// collection-CONTAINS support. The chip's display label and the keyword carried in
/// <see cref="EntityRef.InternalName"/> are the same string.
/// </summary>
public sealed class RecipeIngredientKeywordKindTarget : IReferenceKindTarget
{
    private readonly RecipesTabViewModel _vm;
    private readonly IDiagnosticsSink? _diag;

    public RecipeIngredientKeywordKindTarget(RecipesTabViewModel vm, IDiagnosticsSink? diag = null)
    {
        _vm = vm;
        _diag = diag;
    }

    public EntityKind Kind => EntityKind.RecipeIngredientKeyword;

    public int TabIndex => 1; // same tab as Recipes

    public bool TrySelectByInternalName(string internalName)
    {
        // Quote the keyword so a hyphen, space, or other token boundary inside the tag
        // doesn't break the query parser.
        var query = $"IngredientKeywords CONTAINS \"{internalName}\"";
        _diag?.Info("Silmarillion.Nav", $"RecipeIngredientKeyword.TrySelect '{internalName}' → setting QueryText.");
        // Clear any prior row selection: this navigation expresses a *filter*, not a
        // specific recipe pick. A residual SelectedRow from earlier in-tab navigation
        // would otherwise linger as a stale selection on top of the new filtered list.
        _vm.SelectedRow = null;
        _vm.QueryText = query;
        return true;
    }

    public bool TryOpenInWindow() => false;
}
