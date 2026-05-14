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
    /// <summary>
    /// Not an entity per se — a deep-link target for "open the Recipes tab filtered to recipes
    /// whose ingredient list mentions this keyword tag." InternalName carries the keyword
    /// (e.g. "Crystal"). Dispatched by RecipeIngredientKeywordKindTarget.
    /// </summary>
    RecipeIngredientKeyword,

    /// <summary>
    /// Not an entity per se — a deep-link target for "open the Items tab filtered to items
    /// that satisfy this recipe-slot's keyword constraint." InternalName carries the slot's
    /// <c>ItemKeys</c> list, '+'-joined (singleton slots collapse to a single token).
    /// Dispatched by ItemKeywordKindTarget.
    /// </summary>
    ItemKeyword,

    /// <summary>
    /// Not an entity per se — a deep-link target for "open the Recipes tab filtered to recipes
    /// that consume this item as a direct ingredient." InternalName carries the item's
    /// InternalName. Dispatched by RecipeIngredientItemKindTarget. Mirror of
    /// <see cref="RecipeIngredientKeyword"/> for the item-pivot direction.
    /// </summary>
    RecipeIngredientItem,
}

/// <summary>
/// A lightweight, serialization-friendly pointer to a specific game entity.
/// Consumers pass this to <see cref="IReferenceNavigator.Open"/> to trigger
/// navigation without coupling to any particular detail-view implementation.
/// </summary>
/// <param name="Kind">The category of entity.</param>
/// <param name="InternalName">The entity's unique internal name (e.g. <c>"CraftedLeatherBoots5"</c>).</param>
public sealed record EntityRef(EntityKind Kind, string InternalName)
{
    public static EntityRef Item(string internalName) => new(EntityKind.Item, internalName);
    public static EntityRef Recipe(string internalName) => new(EntityKind.Recipe, internalName);
    public static EntityRef Ability(string internalName) => new(EntityKind.Ability, internalName);
    public static EntityRef Effect(string internalName) => new(EntityKind.Effect, internalName);
    public static EntityRef Npc(string internalName) => new(EntityKind.Npc, internalName);
    public static EntityRef Quest(string internalName) => new(EntityKind.Quest, internalName);
    public static EntityRef Lorebook(string internalName) => new(EntityKind.Lorebook, internalName);
    public static EntityRef Landmark(string internalName) => new(EntityKind.Landmark, internalName);
    public static EntityRef Area(string internalName) => new(EntityKind.Area, internalName);
    public static EntityRef PlayerTitle(string internalName) => new(EntityKind.PlayerTitle, internalName);
    public static EntityRef StorageVault(string internalName) => new(EntityKind.StorageVault, internalName);
    public static EntityRef RecipeIngredientKeyword(string keyword) => new(EntityKind.RecipeIngredientKeyword, keyword);
    public static EntityRef ItemKeyword(string keyword) => new(EntityKind.ItemKeyword, keyword);
    public static EntityRef ItemKeyword(IReadOnlyList<string> itemKeys) => new(EntityKind.ItemKeyword, string.Join('+', itemKeys));
    public static EntityRef RecipeIngredientItem(string itemInternalName) => new(EntityKind.RecipeIngredientItem, itemInternalName);
}

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
