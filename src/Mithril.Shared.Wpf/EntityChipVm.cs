using Mithril.Shared.Reference;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Data-carrying view-model for a cross-link chip rendered by the <c>EntityChip</c> control.
/// Used inside <see cref="ItemDetailContext"/> (item-detail cross-link sections) and recipe
/// detail panes (ingredient/result chips). When <see cref="IsNavigable"/> is false, the chip
/// renders as plain text — drives the graceful degradation for entity kinds that don't yet
/// have a browsable tab.
/// </summary>
public sealed record EntityChipVm(
    string DisplayName,
    int IconId,
    EntityRef Reference,
    bool IsNavigable);

/// <summary>
/// Display-VM for an item source row (NPC vendor, monster drop, quest reward, …) shown in
/// item-detail. Many sources don't map to a v1-tabbed entity kind, so
/// <see cref="EntityReference"/> is nullable and <see cref="IsNavigable"/> may be false even
/// when <see cref="EntityReference"/> is present (e.g. an Npc source until NPCs get a tab).
/// </summary>
public sealed record ItemSourceChipVm(
    string DisplayName,
    string? Detail,
    int? IconId,
    EntityRef? EntityReference,
    bool IsNavigable);
