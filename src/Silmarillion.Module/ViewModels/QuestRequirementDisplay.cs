using Mithril.Shared.Reference;

namespace Silmarillion.ViewModels;

/// <summary>
/// One projected line in a quest's <see cref="QuestRequirementGroup"/>.
/// <para>
/// Two render shapes, chosen at projection time:
/// </para>
/// <list type="bullet">
///   <item><description><b>Prose row:</b> <see cref="Text"/> set, <see cref="ChipName"/> null. Renders as a
///   labelled sentence (<c>"Friends with Joeh (Serbule)"</c>). A trailing arrow button appears
///   when <see cref="IsNavigable"/> is true.</description></item>
///   <item><description><b>Chip row:</b> <see cref="Prefix"/> + <see cref="ChipName"/> set. Renders as
///   <c>"{Prefix} [chip:{ChipName} →]"</c> where the entity name is a proper clickable chip when
///   <see cref="IsNavigable"/> is true. Used for gates that resolve cleanly to a single entity —
///   completed-quest prerequisites, "has item" gates, ability gates.</description></item>
/// </list>
/// <para>
/// <see cref="Text"/> is always populated as the fallback / accessibility-readable representation;
/// the chip path just uses <see cref="Prefix"/>/<see cref="ChipName"/> in preference for display.
/// </para>
/// </summary>
public sealed record QuestRequirementDisplay(
    string Text,
    EntityRef? Reference,
    bool IsNavigable,
    string? Prefix = null,
    string? ChipName = null);

/// <summary>
/// A labelled bundle of related quest-requirement entries. Grouping is by intent (what kind
/// of gate this is from the player's POV) rather than by polymorphic discriminator class —
/// 42 subclass T values collapse to ~8 player-facing buckets so the detail pane reads as
/// a parseable list of prerequisites instead of an undifferentiated bullet wall.
/// </summary>
public sealed record QuestRequirementGroup(
    string Label,
    IReadOnlyList<QuestRequirementDisplay> Requirements);
