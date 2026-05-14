using Mithril.Reference.Models.Quests;

namespace Silmarillion.ViewModels;

/// <summary>
/// Lightweight projection of a <see cref="Quest"/> for the Quests tab card list. The raw POCO
/// has a lot of optional fields (TSysLevel, IsAutoWrapUp, GroupingName, NamedLootProfile, …)
/// that aren't player-facing; this row exposes the player-relevant surface plus the cross-cuts
/// the master list and query box need.
/// <para>
/// <see cref="FavorNpcDisplayName"/> is pre-resolved via <see cref="Mithril.Shared.Reference.IEntityNameResolver"/>
/// so the card row reads as <c>"Joeh"</c> rather than <c>"NPC_Joeh"</c>. The raw FavorNpc
/// internal-name stays on the wrapped <see cref="Quest"/> for the detail-pane chip builder.
/// </para>
/// <para>
/// <see cref="Keywords"/> is wrapped in <see cref="QuestKeywordValue"/> so the query parser
/// can run <c>Keywords CONTAINS "MainStory"</c> against the collection per the
/// <c>IQueryStringValue</c> path shipped in #261.
/// </para>
/// </summary>
public sealed record QuestListRow(
    Quest Quest,
    string InternalName,
    string Name,
    int? Level,
    string? FavorNpcDisplayName,
    string? DisplayedLocation,
    IReadOnlyList<QuestKeywordValue> Keywords,
    bool IsCancellable,
    bool IsGuildQuest,
    bool IsWorkOrder,
    bool IsRepeatable);
