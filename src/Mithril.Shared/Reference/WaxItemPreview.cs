namespace Mithril.Shared.Reference;

/// <summary>
/// Parsed projection of a <c>CraftWaxItem(waxItemTemplate, powerName, tier, durability)</c>
/// entry in <see cref="RecipeEntry.ResultEffects"/>. Reuses the same per-tier
/// <c>EffectDescs</c> rendering pipeline as <see cref="AugmentPreview"/>, but the wax
/// item itself has a finite use count rather than being a permanent enchantment.
/// </summary>
public sealed record WaxItemPreview(
    string WaxItemTemplate,
    string PowerInternalName,
    string? Suffix,
    int Tier,
    int Durability,
    IReadOnlyList<EffectLine> EffectLines)
{
    public string DisplayLine => Suffix is { Length: > 0 }
        ? $"{Suffix} · Tier {Tier} · {Durability} uses"
        : $"{PowerInternalName} · Tier {Tier} · {Durability} uses";
}
