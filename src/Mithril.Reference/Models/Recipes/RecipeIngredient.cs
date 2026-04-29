using System.Collections.Generic;

namespace Mithril.Reference.Models.Recipes;

/// <summary>
/// One slot in a recipe's <c>Ingredients</c> array. Two mutually-exclusive
/// shapes: an <see cref="ItemCode"/>-backed direct item reference, or an
/// <see cref="ItemKeys"/>-backed keyword-matched slot (any item whose
/// <c>Keywords</c> include every listed tag is accepted). Exactly one of the
/// two is set on every entry in the bundled data — consumers should branch
/// on whichever is non-null.
/// </summary>
public sealed class RecipeIngredient
{
    /// <summary>Direct numeric item id (matches <c>item_NNNN</c> in items.json).</summary>
    public long? ItemCode { get; set; }

    /// <summary>Keyword set (AND-matched) for keyword-based ingredient slots.</summary>
    public IReadOnlyList<string>? ItemKeys { get; set; }

    /// <summary>Display label on keyword-matched slots (e.g. "Auxiliary Crystal").</summary>
    public string? Desc { get; set; }

    public int StackSize { get; set; }

    /// <summary>0.0 - 1.0 probability that the ingredient is consumed on a successful craft.</summary>
    public double? ChanceToConsume { get; set; }

    /// <summary>Durability cost for tools used as ingredients.</summary>
    public double? DurabilityConsumed { get; set; }
}
