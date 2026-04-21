namespace Gorgon.Shared.Storage;

/// <summary>
/// A single item entry from a character storage export.
/// </summary>
public sealed record StorageItem(
    int TypeID,
    string Name,
    int StackSize,
    decimal Value,
    string? StorageVault,
    string? Rarity,
    string? Slot,
    int? Level,
    bool IsInInventory,
    bool IsCrafted,
    string? AttunedTo,
    string? Crafter,
    double? Durability,
    int? TransmuteCount,
    int? CraftPoints,
    IReadOnlyList<TsysPower>? TSysPowers,
    string? TSysImbuePower,
    int? TSysImbuePowerTier,
    string? PetHusbandryState);

public sealed record TsysPower(int Tier, string Power);
