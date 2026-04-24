namespace Celebrimbor.Domain;

/// <summary>
/// One line of the user's craft list: a recipe (keyed by InternalName so it
/// survives reference-data refreshes that renumber Keys) and how many batches
/// the user wants to make.
/// </summary>
public sealed class CraftListEntry
{
    public string RecipeInternalName { get; set; } = "";
    public int Quantity { get; set; }
}
