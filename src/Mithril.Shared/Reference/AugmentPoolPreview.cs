namespace Mithril.Shared.Reference;

/// <summary>
/// Lightweight navigation token emitted by <c>ExtractTSysPower</c> and the enchantment-source
/// form of <c>TSysCraftedEquipment</c>. Carries enough metadata for the "Treasure Effects"
/// summary on <c>ItemDetailWindow</c>; the pool viewer expands the full option list lazily
/// when the user clicks "Browse pool" so a 2000-entry profile (<c>"All"</c>) doesn't render
/// eagerly on every detail-window open.
/// </summary>
public sealed record AugmentPoolPreview(
    string SourceLabel,
    string ProfileName,
    int? MinTier,
    int? MaxTier,
    int OptionCount,
    string? RecommendedSkill = null,
    int? CraftingTargetLevel = null,
    int? RolledRarityRank = null,
    string? SourceEquipSlot = null)
{
    /// <summary>
    /// True when the recipe carries an explicit level/tier range — the
    /// <c>ExtractTSysPower</c> shape. The <c>TSysCraftedEquipment</c> shape leaves
    /// the range implicit (it derives from the template's <c>CraftingTargetLevel</c>).
    /// </summary>
    public bool IsExtraction => MinTier.HasValue || MaxTier.HasValue;

    public string TierBracket => (MinTier, MaxTier) switch
    {
        (null, null) => "",
        (int lo, int hi) when lo == hi => $"level {lo}",
        (int lo, int hi) => $"level {lo}-{hi}",
        (int lo, null) => $"level {lo}+",
        (null, int hi) => $"up to level {hi}",
    };

    /// <summary>
    /// Short headline. Extractions surface their level bracket (real, actionable data
    /// the player chooses inputs to control); crafts just show the count, since "tier
    /// follows your craft skill" is common-sense in-game and slightly imprecise (tier
    /// keys off the template's <c>CraftingTargetLevel</c>, not raw skill).
    /// </summary>
    public string DisplayLine => IsExtraction
        ? $"{OptionCount} possible powers · {TierBracket}"
        : $"{OptionCount} possible powers";
}
