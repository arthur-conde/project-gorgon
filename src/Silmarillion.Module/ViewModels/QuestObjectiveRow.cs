namespace Silmarillion.ViewModels;

/// <summary>
/// One step in a quest's <see cref="Mithril.Reference.Models.Quests.Quest.Objectives"/> list.
/// Projects the raw <see cref="Mithril.Reference.Models.Quests.QuestObjective"/> to a row that
/// renders cleanly as a numbered list: a player-facing description plus optional nested
/// requirement chips when the objective itself carries gates (e.g. "Only counts while in combat
/// with elite-level monsters").
/// </summary>
public sealed record QuestObjectiveRow(
    int Index,
    string Description,
    int? Number,
    IReadOnlyList<QuestRequirementGroup> NestedRequirements);
