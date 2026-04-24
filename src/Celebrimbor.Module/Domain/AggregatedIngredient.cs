namespace Celebrimbor.Domain;

/// <summary>
/// One row in the shopping-list view: a single item with its aggregated
/// demand, on-hand count, and where the on-hand copies live.
/// </summary>
public sealed record AggregatedIngredient(
    string ItemInternalName,
    long ItemId,
    string DisplayName,
    int IconId,
    string PrimaryTag,
    int TotalNeeded,
    double ExpectedNeeded,
    int OnHandDetected,
    int? OnHandOverride,
    IReadOnlyList<IngredientLocation> Locations,
    bool IsAlsoRecipe,
    int Depth = 0)
{
    public int EffectiveOnHand => OnHandOverride ?? OnHandDetected;
    public int Remaining => Math.Max(0, TotalNeeded - EffectiveOnHand);
    public bool IsCraftReady => EffectiveOnHand >= TotalNeeded;
}
