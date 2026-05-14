using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> for <see cref="EntityKind.EffectByStackingType"/>.
/// Flips to the Effects tab and pre-populates <see cref="EffectsTabViewModel.QueryText"/>
/// with <c>StackingType = "&lt;value&gt;"</c>. Powers the Effects-tab "Stacks with"
/// section's overflow pill — large stacking groups like <c>"Food"</c> (~326 effects)
/// or <c>"Snack"</c> (~190 effects) collapse the long tail behind a single pill that
/// deep-links to the filtered set.
/// </summary>
public sealed class EffectByStackingTypeKindTarget : IReferenceKindTarget
{
    private readonly EffectsTabViewModel _vm;
    private readonly IDiagnosticsSink? _diag;

    public EffectByStackingTypeKindTarget(EffectsTabViewModel vm, IDiagnosticsSink? diag = null)
    {
        _vm = vm;
        _diag = diag;
    }

    public EntityKind Kind => EntityKind.EffectByStackingType;

    public int TabIndex => 5; // Effects tab

    public bool TrySelectByInternalName(string internalName)
    {
        if (string.IsNullOrEmpty(internalName))
        {
            _diag?.Info("Silmarillion.Nav", "EffectByStackingType.TrySelect '' → empty payload, leaving Effects tab unchanged.");
            return false;
        }

        var escaped = internalName.Replace("\"", "\\\"");
        var query = $"StackingType = \"{escaped}\"";
        _diag?.Info("Silmarillion.Nav", $"EffectByStackingType.TrySelect '{internalName}' → QueryText='{query}'.");
        _vm.SelectedRow = null;
        _vm.QueryText = query;
        return true;
    }

    public bool TryOpenInWindow() => false;
}
