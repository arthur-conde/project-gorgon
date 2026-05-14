using Mithril.Shared.Reference;

namespace Silmarillion.ViewModels;

/// <summary>
/// One projected line in a quest's <see cref="QuestRequirementGroup"/>. Carries a
/// player-readable <see cref="Text"/> (internal names already resolved to display names),
/// an optional <see cref="Reference"/> for navigable chips (a completed-quest gate becomes
/// a clickable quest chip; an inventory-item gate becomes a clickable item chip), and a
/// flag indicating whether the navigator currently has a kind target for the reference's
/// kind (so the renderer can degrade unsupported kinds to plain text per the cookbook's
/// "let CanOpen decide" rule).
/// </summary>
public sealed record QuestRequirementDisplay(
    string Text,
    EntityRef? Reference,
    bool IsNavigable);

/// <summary>
/// A labelled bundle of related quest-requirement entries. Grouping is by intent (what kind
/// of gate this is from the player's POV) rather than by polymorphic discriminator class —
/// 42 subclass T values collapse to ~8 player-facing buckets so the detail pane reads as
/// a parseable list of prerequisites instead of an undifferentiated bullet wall.
/// </summary>
public sealed record QuestRequirementGroup(
    string Label,
    IReadOnlyList<QuestRequirementDisplay> Requirements);
