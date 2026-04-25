namespace Mithril.Shared.Reference;

/// <summary>
/// Parsed projection of an <c>AddItemTSysPowerWax(powerName, tier, durability)</c>
/// entry in <see cref="RecipeEntry.ResultEffects"/>. Sibling of
/// <see cref="AugmentPreview"/> for finite-use applications: the recipe attaches
/// a power tier to a target item that wears off after <see cref="Durability"/>
/// uses, rather than producing a wax item template (which is what
/// <see cref="WaxItemPreview"/> models).
/// </summary>
public sealed record WaxAugmentPreview(
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
