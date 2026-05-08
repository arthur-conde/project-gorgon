namespace Mithril.Shared.Reference;

/// <summary>
/// Slim projection of one entry inside a skill's per-level rewards map. Mirrors
/// <c>Mithril.Reference.Models.Misc.SkillReward</c> at the Mithril.Shared boundary
/// so consumers don't need a transitive reference to Mithril.Reference's POCOs.
/// At least one of the fields is set per row; consumers should null-check the
/// fields they care about.
/// </summary>
public sealed record SkillRewardEntry(
    string? BonusToSkill,
    IReadOnlyList<string> Ability,
    string? Notes,
    string? Recipe);
