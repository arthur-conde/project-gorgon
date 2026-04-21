namespace Pippin.Domain;

/// <summary>
/// One food item from the CDN catalog, enriched with parsed type/level info.
/// </summary>
public sealed record FoodEntry(
    long ItemId,
    string Name,
    int IconId,
    string FoodType,
    int FoodLevel,
    int GourmandLevelReq,
    IReadOnlyList<string> DietaryTags);
