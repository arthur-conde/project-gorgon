using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> for <see cref="EntityKind.ItemByKeyword"/>.
/// Flips to the Items tab and pre-populates <see cref="ItemsTabViewModel.QueryText"/>
/// with <c>Keywords CONTAINS "&lt;tag&gt;"</c>, where the tag is the
/// <see cref="EntityRef.InternalName"/> payload.
/// <para>
/// This is the single-keyword Items <em>filter pivot</em> — a legitimate 1:1 per the
/// #318 chip-vs-popup rule (one tag → one filtered view), the symmetric Items-side twin
/// of <see cref="EffectKeywordKindTarget"/>. It is deliberately <em>not</em> the retired
/// #270 <c>ItemKeywordKindTarget</c> (the recipe-detail keyword <em>fan-out</em>, migrated
/// to a provenance popup fed <c>ItemsByRecipeKeywordSlotWithReason</c> in #318 slice 4):
/// no '+'-joined composite slot key, no <c>ItemKeywordQueryMapper</c> re-derivation, no
/// dual-derivation surface. Singleton payload only — every restored consumer
/// (Ability-detail <c>ItemKeywordReqs</c> / ammo keywords, NPCs-tab Store-cap /
/// Consignment keyword chips) carries exactly one tag per chip.
/// </para>
/// <para>
/// Restored in #327 (the chips degraded to non-navigable plain text in #326 when the
/// double-duty <c>ItemKeyword</c> kind was retired for its fan-out use). #332 separately
/// tracks partitioning the <c>HasHands</c>/<c>Unarmed</c> hand-state pseudo-keywords out
/// of the Ability <c>ItemKeywordReqs</c> chip (they match zero items → dead pivot); that
/// pre-existing data-classification defect is intentionally out of scope here.
/// </para>
/// </summary>
public sealed class ItemByKeywordKindTarget : IReferenceKindTarget
{
    private readonly ItemsTabViewModel _vm;
    private readonly IDiagnosticsSink? _diag;

    public ItemByKeywordKindTarget(ItemsTabViewModel vm, IDiagnosticsSink? diag = null)
    {
        _vm = vm;
        _diag = diag;
    }

    public EntityKind Kind => EntityKind.ItemByKeyword;

    public int TabIndex => 0; // Items tab

    public bool TrySelectByInternalName(string internalName)
    {
        if (string.IsNullOrEmpty(internalName))
        {
            _diag?.Info("Silmarillion.Nav", "ItemByKeyword.TrySelect '' → empty payload, leaving Items tab unchanged.");
            return false;
        }

        var escaped = internalName.Replace("\"", "\\\"");
        var query = $"Keywords CONTAINS \"{escaped}\"";
        _diag?.Info("Silmarillion.Nav", $"ItemByKeyword.TrySelect '{internalName}' → QueryText='{query}'.");
        // Drop any stale detail selection so the filtered list doesn't render with a row
        // hidden by (or inconsistent with) the new filter — mirrors EffectKeywordKindTarget
        // and the retired ItemKeywordKindTarget's filter-only contract.
        _vm.SelectedItem = null;
        _vm.QueryText = query;
        return true;
    }

    public bool TryOpenInWindow() => false;
}
