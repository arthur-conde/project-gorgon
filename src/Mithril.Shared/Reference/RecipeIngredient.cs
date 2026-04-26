namespace Mithril.Shared.Reference;

/// <summary>
/// Base type for a single ingredient slot on a recipe. Two concrete shapes:
/// <see cref="RecipeItemIngredient"/> names a specific item by code, while
/// <see cref="RecipeKeywordIngredient"/> names a keyword set (any item whose
/// <see cref="ItemEntry.Keywords"/> includes every listed tag satisfies the slot).
/// </summary>
public abstract record RecipeIngredient(int StackSize, float? ChanceToConsume);

/// <summary>Ingredient slot bound to a specific item id.</summary>
public sealed record RecipeItemIngredient(long ItemCode, int StackSize, float? ChanceToConsume)
    : RecipeIngredient(StackSize, ChanceToConsume);

/// <summary>
/// Ingredient slot satisfied by any item carrying every keyword in <see cref="ItemKeys"/>
/// (AND-matched). <see cref="Desc"/> is the recipe's display label, e.g. "Auxiliary Crystal".
/// </summary>
public sealed record RecipeKeywordIngredient(
    IReadOnlyList<string> ItemKeys,
    string? Desc,
    int StackSize,
    float? ChanceToConsume)
    : RecipeIngredient(StackSize, ChanceToConsume);
