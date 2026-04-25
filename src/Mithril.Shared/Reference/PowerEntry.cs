namespace Mithril.Shared.Reference;

/// <summary>
/// Typed projection of a tsysclientinfo.json entry — one power that can augment an item,
/// with a dictionary of tier-specific effect descriptions. Looked up by
/// <see cref="InternalName"/> from recipe strings like <c>AddItemTSysPower(InternalName, tier)</c>.
/// </summary>
/// <remarks>
/// <see cref="Suffix"/> is nullable because only drop/loot powers carry a display suffix
/// like "of Archery". Deterministic infusion powers (referenced by most
/// <c>AddItemTSysPower</c> recipes) typically have no suffix — UI falls back to
/// <see cref="InternalName"/> in that case.
/// </remarks>
public sealed record PowerEntry(
    string InternalName,
    string Skill,
    IReadOnlyList<string> Slots,
    string? Suffix,
    IReadOnlyDictionary<int, PowerTier> Tiers);
