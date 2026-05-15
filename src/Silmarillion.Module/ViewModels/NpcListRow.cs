using Mithril.Reference.Models.Npcs;

namespace Silmarillion.ViewModels;

/// <summary>
/// Lightweight projection of an <see cref="Npc"/> for the NPCs tab card list. The raw POCO
/// has no <c>InternalName</c> field (it's the JSON envelope key), so the row carries it
/// alongside pre-resolved display names. <see cref="ServiceTypes"/> is the flat, deduplicated
/// set of <c>NpcService.Type</c> values across the NPC's services — queryable via the engine's
/// collection-<c>CONTAINS</c> path (powers <c>ServiceTypes CONTAINS "Store"</c> and friends).
/// <para>
/// <see cref="AreaName"/> is the area envelope key (e.g. <c>"AreaSerbule"</c>), distinct from
/// the friendly-name fallback in <see cref="AreaDisplayName"/>. Exposed so the synthetic
/// <c>NpcByArea</c> kind target can pre-fill <c>AreaName = "AreaSerbule"</c> exact-match
/// queries from the Areas tab's overflow pill without false-positives on friendly-name
/// substrings (e.g. <c>"Serbule"</c> ≠ <c>"Serbule Hills"</c>).
/// </para>
/// </summary>
public sealed record NpcListRow(
    Npc Npc,
    string InternalName,
    string Name,
    string AreaName,
    string AreaDisplayName,
    IReadOnlyList<NpcServiceTypeValue> ServiceTypes);
