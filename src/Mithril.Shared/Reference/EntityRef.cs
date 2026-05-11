namespace Mithril.Shared.Reference;

/// <summary>
/// Identifies a kind of game entity that can be navigated to via
/// <see cref="IReferenceNavigator"/>.
/// </summary>
public enum EntityKind
{
    Item,
    Recipe,
    Ability,
    Effect,
    Npc,
    Quest,
    Lorebook,
    Landmark,
    Area,
    PlayerTitle,
    StorageVault,
}

/// <summary>
/// A lightweight, serialization-friendly pointer to a specific game entity.
/// Consumers pass this to <see cref="IReferenceNavigator.Open"/> to trigger
/// navigation without coupling to any particular detail-view implementation.
/// </summary>
/// <param name="Kind">The category of entity.</param>
/// <param name="InternalName">The entity's unique internal name (e.g. <c>"CraftedLeatherBoots5"</c>).</param>
public sealed record EntityRef(EntityKind Kind, string InternalName);
