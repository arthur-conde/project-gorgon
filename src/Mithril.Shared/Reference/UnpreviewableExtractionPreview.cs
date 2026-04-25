namespace Mithril.Shared.Reference;

/// <summary>
/// Parsed projection of an <c>ExtractTSysPower(cubeItem, craftingSkill, minTier, maxTier)</c>
/// entry in <see cref="RecipeEntry.ResultEffects"/>. Distinct from
/// <see cref="AugmentPoolPreview"/> because the rolled outcome is fundamentally
/// indeterminate at preview time: the recipe consumes a player-provided augment
/// cube (e.g. a <c>MainHandAugment</c>) whose specific power was minted by an
/// earlier <c>AddItemTSysPower</c> recipe. The recipe metadata can't enumerate
/// the possible outcomes — they depend on which cube the player slots in at
/// craft time.
/// <para>
/// Renders as a "not available for preview" affordance so the user understands
/// the recipe isn't broken; it just can't be modeled out of game.
/// </para>
/// </summary>
public sealed record UnpreviewableExtractionPreview(
    string SourceItemInternalName,
    string SourceItemDisplayName,
    int? IconId,
    int MinTier,
    int MaxTier)
{
    public string DisplayLine =>
        $"Extracts a power from {SourceItemDisplayName} (Level {MinTier}–{MaxTier}). " +
        "Outcome depends on the cube provided at craft time — preview not available.";
}
