namespace Silmarillion.ViewModels;

/// <summary>
/// One projected reward in a quest's <see cref="QuestRewardGroup"/>. Carries player-readable
/// <see cref="Text"/> (currency-formatted, skill display-name resolved). Cross-link rewards
/// (items, recipes) ship as <see cref="EntityChipVm"/> chips in a separate collection on
/// the detail VM, not here.
/// </summary>
public sealed record QuestRewardDisplay(string Text);

/// <summary>
/// A labelled bundle of related quest rewards. Grouping is by player-facing intent — XP,
/// currency, item, recipe — rather than by raw subclass. Empty groups don't render.
/// </summary>
public sealed record QuestRewardGroup(
    string Label,
    IReadOnlyList<QuestRewardDisplay> Rewards);
