namespace Mithril.Shared.Reference;

/// <summary>
/// Lightweight navigation token emitted by <c>ExtractTSysPower</c> and the enchantment-source
/// form of <c>TSysCraftedEquipment</c>. Carries enough metadata for the "Possible augments"
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
    int? RolledRarityRank = null)
{
    /// <summary>
    /// True when the recipe carries an explicit level/tier range — the
    /// <c>ExtractTSysPower</c> shape. The other shape (<c>TSysCraftedEquipment</c>
    /// enchantment) leaves tier to the craft skill rather than rolling it.
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
    /// Short headline matched to the recipe's mechanic — distinguishes "tier comes
    /// from the craft" (enchantment) from "tier is bounded by the level range"
    /// (extraction), since the same OptionCount means different things.
    /// </summary>
    public string DisplayLine => IsExtraction
        ? $"{OptionCount} possible powers · {TierBracket}"
        : $"{OptionCount} possible powers · tier follows your craft skill";

    /// <summary>Pool-name hint for the curious; rendered as a smaller secondary line.</summary>
    public string ProfileHint => $"Profile: {ProfileName}";
}
