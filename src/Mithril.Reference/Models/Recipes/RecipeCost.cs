namespace Mithril.Reference.Models.Recipes;

/// <summary>
/// One entry in a recipe's <c>Costs</c> array — currency required to craft.
/// </summary>
public sealed class RecipeCost
{
    public string? Currency { get; set; }
    public int Price { get; set; }
}
