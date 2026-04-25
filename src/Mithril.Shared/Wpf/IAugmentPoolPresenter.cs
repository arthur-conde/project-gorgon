namespace Mithril.Shared.Wpf;

/// <summary>
/// Opens the dedicated augment-pool viewer for a given <c>tsysprofiles</c> profile.
/// Implemented by Celebrimbor (which owns the viewer UI). Consumers in
/// <c>Mithril.Shared</c> resolve this as an <em>optional</em> dependency: when the
/// Celebrimbor module isn't loaded, no implementation is registered, and the
/// "Browse pool" button on <see cref="ItemDetailWindow"/> hides itself rather than
/// dispatching to a no-op.
/// </summary>
public interface IAugmentPoolPresenter
{
    /// <summary>
    /// Opens the pool viewer.
    /// </summary>
    /// <param name="sourceLabel">Human-readable header — e.g. "Possible rolls for Crafted Leather Boots".</param>
    /// <param name="profileName">Profile key into <c>tsysprofiles.json</c>.</param>
    /// <param name="minTier">Inclusive lower bound for the tier filter, or <see langword="null"/> for unbounded.</param>
    /// <param name="maxTier">Inclusive upper bound for the tier filter, or <see langword="null"/> for unbounded.</param>
    /// <param name="recommendedSkill">
    /// Optional skill name (e.g. "Werewolf", "Sword") to pre-filter the pool to powers
    /// whose <c>Skill</c> matches. Sourced from the source item's first non-craft skill
    /// prereq so a Werewolf-armor enchant defaults to Werewolf rolls.
    /// </param>
    /// <param name="craftingTargetLevel">
    /// Optional gear level used to pre-filter the pool to power tiers whose
    /// <c>[MinLevel, MaxLevel]</c> bracket contains this value. For an enchantment recipe
    /// this is the template's <c>CraftingTargetLevel</c>; the user can clear or widen
    /// the resulting query to inspect tiers outside the eligible band.
    /// </param>
    /// <param name="rolledRarityRank">
    /// Optional rank of the rolled gear rarity (Uncommon=1, Rare=2, Exceptional=3,
    /// Epic=4) used to pre-filter the pool to power tiers whose <c>MinRarity</c> rank
    /// is at-most this value. For an enchant recipe this comes from arg2 of
    /// <c>TSysCraftedEquipment</c> (0 → Uncommon, 1 → Rare via Max-Enchanting).
    /// </param>
    void Show(string sourceLabel, string profileName, int? minTier = null, int? maxTier = null, string? recommendedSkill = null, int? craftingTargetLevel = null, int? rolledRarityRank = null);
}
