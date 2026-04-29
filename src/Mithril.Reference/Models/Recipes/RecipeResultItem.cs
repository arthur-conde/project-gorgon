namespace Mithril.Reference.Models.Recipes;

/// <summary>
/// One slot in a recipe's <c>ResultItems</c> or <c>ProtoResultItems</c> array.
/// Crafted-equipment recipes use <c>ProtoResultItems</c> as a fallback when
/// <c>ResultItems</c> is empty; the procedural <c>ResultEffects</c> on the
/// recipe encode the tier/subtype of the actual finished item.
/// </summary>
public sealed class RecipeResultItem
{
    public long ItemCode { get; set; }
    public int StackSize { get; set; }

    /// <summary>0.0 - 100.0 chance that the result drops; null = always.</summary>
    public double? PercentChance { get; set; }

    /// <summary>If true, the crafted item is bound to the crafter on creation.</summary>
    public bool? AttuneToCrafter { get; set; }
}
