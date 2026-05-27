using Microsoft.Extensions.Logging;
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
    private readonly ILogger? _logger;

    public EffectByStackingTypeKindTarget(EffectsTabViewModel vm, ILogger? logger = null)
    {
        _vm = vm;
        _logger = logger;
    }

    public EntityKind Kind => EntityKind.EffectByStackingType;

    public int TabIndex => 5; // Effects tab

    public bool TrySelectByInternalName(string internalName)
    {
        if (string.IsNullOrEmpty(internalName))
        {
            _logger?.LogTrace("EffectByStackingType.TrySelect '' → empty payload, leaving Effects tab unchanged.");
            return false;
        }

        var escaped = internalName.Replace("\"", "\\\"");
        var query = $"StackingType = \"{escaped}\"";
        _logger?.LogTrace("EffectByStackingType.TrySelect '{InternalName}' → QueryText='{QueryText}'.", internalName, query);
        _vm.SelectedRow = null;
        _vm.QueryText = query;
        return true;
    }

    public bool TryOpenInWindow() => false;
}
