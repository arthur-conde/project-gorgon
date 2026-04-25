namespace Mithril.Shared.Reference;

/// <summary>
/// One entry from <c>sources_items.json</c> — identifies how an item can be obtained.
/// Vendor entries carry the NPC key in <see cref="Npc"/>; other source types (Recipe,
/// HangOut, NpcGift, Quest, Barter, Monster, Angling, …) may leave Npc empty and carry
/// type-specific context in <see cref="Context"/>.
/// </summary>
public sealed record ItemSource(string Type, string? Npc, string? Context);
