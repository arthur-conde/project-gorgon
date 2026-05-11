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

/// <summary>
/// Describes what kind of navigation action produced a <see cref="NavigatedEventArgs"/>.
/// </summary>
public enum NavigationKind
{
    /// <summary>A new entity was opened, pushing onto the back stack and clearing the forward stack.</summary>
    Open,

    /// <summary>The user navigated back through history.</summary>
    Back,

    /// <summary>The user navigated forward through history.</summary>
    Forward,
}

/// <summary>
/// Event data fired by <see cref="IReferenceNavigator.Navigated"/> on every state change.
/// </summary>
/// <param name="Previous">The entity that was current before this navigation, or <see langword="null"/> if there was none.</param>
/// <param name="Current">The entity that is current after this navigation, or <see langword="null"/> if the history is now empty.</param>
/// <param name="Kind">What kind of navigation produced this event.</param>
public sealed record NavigatedEventArgs(
    EntityRef? Previous,
    EntityRef? Current,
    NavigationKind Kind);
