using Mithril.Reference.Models.Abilities;

namespace Silmarillion.ViewModels;

/// <summary>
/// Lightweight projection of an <see cref="Ability"/> for the Abilities tab card list. The raw
/// POCO has ~80 properties; this row exposes the player-relevant cross-cuts plus the
/// collection-shaped <see cref="Keywords"/> field the query box uses for <c>CONTAINS</c>
/// filtering (powers <c>Keywords CONTAINS "Attack"</c> and friends via the
/// <see cref="Mithril.Reference.IQueryStringValue"/> path shipped in #261).
/// <para>
/// <see cref="Skill"/> is pre-resolved via <see cref="Mithril.Shared.Reference.IReferenceDataService.Skills"/>
/// so the card reads as <c>"Sword"</c> rather than <c>"Sword"</c> (id-shaped key already, but
/// the same pipeline handles cases like <c>"AncillaryArmorAugmentBrewing"</c> →
/// <c>"Armor Augment Brewing"</c>). Falls back to the raw <see cref="Ability.Skill"/> or
/// <c>"(unknown)"</c> when no skill entry is registered.
/// </para>
/// </summary>
public sealed record AbilityListRow(
    Ability Ability,
    string InternalName,
    string Name,
    string Skill,
    int Level,
    string? Rank,
    IReadOnlyList<IngredientKeywordValue> Keywords,
    IReadOnlyList<IngredientKeywordValue> EffectKeywordReqs,
    double ResetTimeSeconds,
    int IconID)
{
    /// <summary>
    /// Card-secondary line — "Sword 7" format per the cookbook's master-list precedent. Bound by
    /// the <c>AbilityCardTemplate</c> as a single <see cref="System.Windows.Documents.Run"/> to
    /// avoid the multi-<c>Run</c> whitespace inflation that read as tab-spacing.
    /// </summary>
    public string SkillLevelDisplay => $"{Skill} {Level}";
}
