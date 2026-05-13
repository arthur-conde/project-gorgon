namespace Mithril.Shared.Reference;

/// <summary>
/// One entry from <c>sources_recipes.json</c> — identifies how a recipe is acquired
/// (taught by an NPC, granted by a scroll/effect, awarded by a quest, …). Mirrors
/// <see cref="ItemSource"/>; the two are parallel records rather than a shared base
/// so <see cref="IReferenceDataService"/> stays self-documenting.
/// </summary>
public sealed record RecipeSource(string Type, string? Npc, string? Context);
