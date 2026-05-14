namespace Mithril.Shared.Reference;

/// <summary>
/// One entry from <c>sources_abilities.json</c> — identifies how an ability is acquired
/// (taught by an NPC, granted by a quest, awarded by a skill milestone, …). Mirrors
/// <see cref="ItemSource"/> and <see cref="RecipeSource"/>; the three are parallel records
/// rather than a shared base so <see cref="IReferenceDataService"/> stays self-documenting.
/// </summary>
public sealed record AbilitySource(string Type, string? Npc, string? Context);
