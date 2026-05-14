using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> for <see cref="EntityKind.AbilityByEffectKeyword"/>.
/// Flips to the Abilities tab and pre-populates <see cref="AbilitiesTabViewModel.QueryText"/>
/// with <c>EffectKeywordReqs CONTAINS "&lt;tag&gt;"</c>. Powers the Effects-tab
/// "Required by abilities" section's overflow pill when the chip cluster exceeds
/// <c>SilmarillionSettings.RequiredByAbilitiesChipCap</c>.
/// <para>
/// Mirror of <see cref="EffectKeywordKindTarget"/> for the abilities-pivot direction:
/// EffectKeyword filters Effects whose Keywords contain the tag; this one filters
/// Abilities whose EffectKeywordReqs contain the tag. Both work because both row types
/// surface the relevant collection field as <see cref="IngredientKeywordValue"/>
/// wrappers for the query engine's <c>CONTAINS</c> path.
/// </para>
/// </summary>
public sealed class AbilityByEffectKeywordKindTarget : IReferenceKindTarget
{
    private readonly AbilitiesTabViewModel _vm;
    private readonly IDiagnosticsSink? _diag;

    public AbilityByEffectKeywordKindTarget(AbilitiesTabViewModel vm, IDiagnosticsSink? diag = null)
    {
        _vm = vm;
        _diag = diag;
    }

    public EntityKind Kind => EntityKind.AbilityByEffectKeyword;

    public int TabIndex => 4; // Abilities tab

    public bool TrySelectByInternalName(string internalName)
    {
        if (string.IsNullOrEmpty(internalName))
        {
            _diag?.Info("Silmarillion.Nav", "AbilityByEffectKeyword.TrySelect '' → empty payload, leaving Abilities tab unchanged.");
            return false;
        }

        var escaped = internalName.Replace("\"", "\\\"");
        var query = $"EffectKeywordReqs CONTAINS \"{escaped}\"";
        _diag?.Info("Silmarillion.Nav", $"AbilityByEffectKeyword.TrySelect '{internalName}' → QueryText='{query}'.");
        _vm.SelectedRow = null;
        _vm.QueryText = query;
        return true;
    }

    public bool TryOpenInWindow() => false;
}
