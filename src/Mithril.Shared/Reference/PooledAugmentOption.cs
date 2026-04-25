namespace Mithril.Shared.Reference;

/// <summary>
/// One row in the augment-pool viewer. Built lazily by <c>AugmentPoolViewModel</c> from
/// (profile → power list → tier-flattened) and shaped to match <see cref="AugmentPreview"/>'s
/// display style so the pool viewer can reuse the same effect-line rendering as the
/// Augmentation section on <c>ItemDetailWindow</c>.
/// </summary>
/// <remarks>
/// <see cref="MinLevel"/> / <see cref="MaxLevel"/> are the gear-level bracket inside which
/// this tier is eligible to roll. <see cref="SkillLevelPrereq"/> is the wearer skill level
/// required for the rolled buff to take effect once equipped. <see cref="MinRarity"/> gates
/// by gear rarity. The grid surfaces these as columns so users can filter to "what can roll
/// on my craft" via the query box.
/// </remarks>
public sealed record PooledAugmentOption(
    string PowerInternalName,
    string? Suffix,
    string Skill,
    int Tier,
    IReadOnlyList<EffectLine> EffectLines,
    int? MinLevel = null,
    int? MaxLevel = null,
    string? MinRarity = null,
    int? SkillLevelPrereq = null)
{
    public string DisplayLine => Suffix is { Length: > 0 }
        ? $"{Suffix} · Tier {Tier}"
        : $"{PowerInternalName} · Tier {Tier}";

    /// <summary>
    /// Ordinal rank of <see cref="MinRarity"/> for query-language comparisons:
    /// Uncommon=1, Rare=2, Exceptional=3, Epic=4. Common is unused in
    /// <c>tsysclientinfo</c>; Common gear isn't enchantable. A tier with rank
    /// <c>R</c> rolls only when the gear's rolled rarity is rank <c>≥ R</c>.
    /// </summary>
    public int MinRarityRank => MinRarity switch
    {
        "Uncommon" => 1,
        "Rare" => 2,
        "Exceptional" => 3,
        "Epic" => 4,
        _ => 0,
    };
}
