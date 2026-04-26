namespace Mithril.Shared.Storage;

/// <summary>
/// One slice of an item's on-hand presence: the item's identity, the storage
/// location it's sitting in, and how many copies live there. The Sources
/// window groups these by <see cref="Label"/> so a keyword-matched ingredient
/// row (e.g. "any Crystal") can show per-item breakdown per chest instead of
/// a flat repeated list.
/// </summary>
public sealed record IngredientLocation(
    string Label,
    int Quantity,
    string ItemInternalName,
    string DisplayName,
    int IconId);
