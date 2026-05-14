using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> for <see cref="EntityKind.ItemKeyword"/>.
/// Flips to the Items tab and pre-populates <see cref="ItemsTabViewModel.QueryText"/>
/// with an AND-joined query derived from the recipe slot's <c>ItemKeys</c> list.
/// <see cref="EntityRef.InternalName"/> carries the slot's keys '+'-joined; this
/// target splits, runs <see cref="ItemKeywordQueryMapper"/>, and on success applies
/// the resulting filter (clearing any prior <see cref="ItemsTabViewModel.SelectedItem"/>
/// so the filtered list doesn't render with a stale selection).
///
/// Closes the symmetry started by <see cref="RecipeIngredientKeywordKindTarget"/>
/// for the reverse direction (item-detail "Used as" chips).
/// </summary>
public sealed class ItemKeywordKindTarget : IReferenceKindTarget
{
    private readonly ItemsTabViewModel _vm;
    private readonly IDiagnosticsSink? _diag;

    public ItemKeywordKindTarget(ItemsTabViewModel vm, IDiagnosticsSink? diag = null)
    {
        _vm = vm;
        _diag = diag;
    }

    public EntityKind Kind => EntityKind.ItemKeyword;

    public int TabIndex => 0; // Items tab

    public bool TrySelectByInternalName(string internalName)
    {
        var itemKeys = internalName.Split('+');
        if (!ItemKeywordQueryMapper.TryBuildQuery(itemKeys, out var query))
        {
            // Defensive: the chip-builder is the gate (it doesn't emit a navigable chip
            // when the mapper fails), so we only reach here if a deep-link or hand-built
            // EntityRef snuck through with an unmappable slot.
            _diag?.Info("Silmarillion.Nav", $"ItemKeyword.TrySelect '{internalName}' → unmappable, leaving Items tab unchanged.");
            return false;
        }

        _diag?.Info("Silmarillion.Nav", $"ItemKeyword.TrySelect '{internalName}' → QueryText='{query}'.");
        _vm.SelectedItem = null;
        _vm.QueryText = query;
        return true;
    }

    public bool TryOpenInWindow() => false;
}
