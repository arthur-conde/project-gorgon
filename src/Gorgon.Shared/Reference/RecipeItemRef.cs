namespace Gorgon.Shared.Reference;

/// <summary>
/// A reference to an item used in a recipe — either as an ingredient or result.
/// </summary>
public sealed record RecipeItemRef(long ItemCode, int StackSize, float? ChanceToConsume);
