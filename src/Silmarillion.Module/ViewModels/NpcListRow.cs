using Mithril.Reference.Models.Npcs;

namespace Silmarillion.ViewModels;

/// <summary>
/// Lightweight projection of an <see cref="Npc"/> for the NPCs tab card list. The raw POCO
/// has no <c>InternalName</c> field (it's the JSON envelope key), so the row carries it
/// alongside pre-resolved display names. <see cref="ServiceTypes"/> is the flat, deduplicated
/// set of <c>NpcService.Type</c> values across the NPC's services — queryable via the engine's
/// collection-<c>CONTAINS</c> path (powers <c>ServiceTypes CONTAINS "Store"</c> and friends).
/// </summary>
public sealed record NpcListRow(
    Npc Npc,
    string InternalName,
    string Name,
    string AreaDisplayName,
    IReadOnlyList<NpcServiceTypeValue> ServiceTypes);
