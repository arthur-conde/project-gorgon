using Mithril.Shared.Reference;

namespace Silmarillion.ViewModels;

/// <summary>
/// One projected reward in a quest's <see cref="QuestRewardGroup"/>. Two render shapes, mirroring
/// <see cref="QuestRequirementDisplay"/>:
/// <list type="bullet">
///   <item><description><b>Prose row:</b> <see cref="Text"/> set, <see cref="ChipName"/> null. Used for
///   value-shaped rewards (XP amounts, currency, favor deltas) where the row is fully a sentence.</description></item>
///   <item><description><b>Chip row:</b> <see cref="Prefix"/> + <see cref="ChipName"/> set. Used for entity-shaped
///   rewards (effects bestowing a lorebook, ability, title, recipe) so each lights up the moment its
///   target tab ships. <see cref="IsNavigable"/> = <see cref="IReferenceNavigator.CanOpen"/> at projection
///   time; <see cref="Text"/> always populated as the fallback / accessibility-readable representation.</description></item>
/// </list>
/// Note: item / recipe rewards from the typed <c>Rewards_Items</c> / <c>Rewards</c> fields ship as
/// <see cref="Mithril.Shared.Wpf.EntityChipVm"/> chips in separate top-level collections on the detail
/// VM (<c>RewardItemChips</c>, <c>RewardRecipeChips</c>); only the *effect-string* rewards land here.
/// </summary>
public sealed record QuestRewardDisplay(
    string Text,
    EntityRef? Reference = null,
    bool IsNavigable = false,
    string? Prefix = null,
    string? ChipName = null);

/// <summary>
/// A labelled bundle of related quest rewards. Grouping is by player-facing intent — XP,
/// currency, item, recipe — rather than by raw subclass. Empty groups don't render.
/// </summary>
public sealed record QuestRewardGroup(
    string Label,
    IReadOnlyList<QuestRewardDisplay> Rewards);
