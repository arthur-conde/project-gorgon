using Microsoft.Extensions.Logging;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> for <see cref="EntityKind.EffectKeyword"/>.
/// Flips to the Effects tab and pre-populates <see cref="EffectsTabViewModel.QueryText"/>
/// with <c>Keywords CONTAINS "&lt;tag&gt;"</c>, where the tag is the
/// <see cref="EntityRef.InternalName"/> payload.
/// <para>
/// Singleton-payload only — effect-keyword references in the catalogue are always single
/// tags (Ability.EffectKeywordReqs, AbilityConditionalKeyword.EffectKeywordMustExist,
/// HasEffectKeywordRequirement.Keyword, etc.) so no '+'-joined composite form is needed.
/// Mirrors the shape of <see cref="ItemKeywordKindTarget"/> for the effect-pivot direction.
/// </para>
/// </summary>
public sealed class EffectKeywordKindTarget : IReferenceKindTarget
{
    private readonly EffectsTabViewModel _vm;
    private readonly ILogger? _logger;

    public EffectKeywordKindTarget(EffectsTabViewModel vm, ILogger? logger = null)
    {
        _vm = vm;
        _logger = logger;
    }

    public EntityKind Kind => EntityKind.EffectKeyword;

    public int TabIndex => 5; // Effects tab

    public bool TrySelectByInternalName(string internalName)
    {
        if (string.IsNullOrEmpty(internalName))
        {
            _logger?.LogTrace("EffectKeyword.TrySelect '' → empty payload, leaving Effects tab unchanged.");
            return false;
        }

        var escaped = internalName.Replace("\"", "\\\"");
        var query = $"Keywords CONTAINS \"{escaped}\"";
        _logger?.LogTrace("EffectKeyword.TrySelect '{InternalName}' → QueryText='{QueryText}'.", internalName, query);
        _vm.SelectedRow = null;
        _vm.QueryText = query;
        return true;
    }

    public bool TryOpenInWindow() => false;
}
