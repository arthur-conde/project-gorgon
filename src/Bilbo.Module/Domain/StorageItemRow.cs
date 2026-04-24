namespace Bilbo.Domain;

/// <summary>
/// Flattened, display-ready row for the storage DataGrid.
/// </summary>
public sealed record StorageItemRow(
    string Name,
    string Location,
    int StackSize,
    decimal UnitValue,
    decimal TotalValue,
    string? Rarity,
    string? Slot,
    int? Level,
    int ModCount,
    string? AttunedTo,
    bool IsCrafted,
    int TypeID,
    int IconId,
    string InternalName);
