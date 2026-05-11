using System.Collections.Generic;

namespace Mithril.Reference.Models.Recipes;

/// <summary>
/// One slot in a recipe's <c>Ingredients</c> array. Two concrete shapes exist —
/// <see cref="RecipeItemIngredient"/> binds the slot to a specific item id, and
/// <see cref="RecipeKeywordIngredient"/> accepts any item whose <c>Keywords</c>
/// include every listed tag (AND-matched). The JSON shape carries no explicit
/// discriminator field; <see cref="Serialization.Converters.RecipeIngredientConverter"/>
/// picks the subclass at deserialize time based on which of <c>ItemCode</c> or
/// <c>ItemKeys</c> is present.
/// </summary>
public abstract class RecipeIngredient
{
    public int StackSize { get; set; }

    /// <summary>0.0 - 1.0 probability that the ingredient is consumed on a successful craft.</summary>
    public double? ChanceToConsume { get; set; }

    /// <summary>Durability cost for tools used as ingredients.</summary>
    public double? DurabilityConsumed { get; set; }
}

/// <summary>
/// Ingredient slot bound to a specific item by numeric id. <see cref="ItemCode"/>
/// matches <c>item_NNNN</c> in items.json.
/// </summary>
public sealed class RecipeItemIngredient : RecipeIngredient
{
    public long ItemCode { get; set; }
}

/// <summary>
/// Ingredient slot satisfied by any item carrying every keyword in <see cref="ItemKeys"/>
/// (AND-matched). <see cref="Desc"/> is the recipe's display label for the slot
/// (e.g. <c>"Auxiliary Crystal"</c>).
/// </summary>
public sealed class RecipeKeywordIngredient : RecipeIngredient
{
    public IReadOnlyList<string> ItemKeys { get; set; } = [];
    public string? Desc { get; set; }
}
