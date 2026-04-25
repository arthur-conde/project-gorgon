namespace Mithril.Shared.Reference;

/// <summary>
/// A parsed keyword from an item. Raw form is "VegetarianDish=84" where
/// Tag is "VegetarianDish" and Quality is 84. Keywords without "=N" have Quality = 0.
/// </summary>
public sealed record ItemKeyword(string Tag, int Quality);

/// <summary>
/// Slim projection of one entry in items.json. Matches the fields the HTML
/// helper extracts, which covers icon rendering, stack-aware tooling,
/// seed-to-crop name resolution, and NPC gift keyword matching.
/// </summary>
public sealed record ItemEntry(
    long Id,
    string Name,
    string InternalName,
    int MaxStackSize,
    int IconId,
    IReadOnlyList<ItemKeyword> Keywords,
    string? EquipSlot = null,
    IReadOnlyList<string>? SkillPrereqs = null,
    decimal Value = 0,
    string? FoodDesc = null,
    IReadOnlyDictionary<string, int>? SkillReqs = null,
    IReadOnlyList<string>? EffectDescs = null,
    string? Description = null);
