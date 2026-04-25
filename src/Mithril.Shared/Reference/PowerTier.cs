namespace Mithril.Shared.Reference;

/// <summary>
/// One tier within a <see cref="PowerEntry"/>. <see cref="Tier"/> is the numeric suffix
/// of the <c>id_N</c> key in tsysclientinfo.json; <see cref="EffectDescs"/> uses the same
/// <c>{TOKEN}{value}</c> format as items.json and resolves via <see cref="EffectDescsRenderer"/>.
/// </summary>
/// <remarks>
/// <see cref="MinLevel"/> / <see cref="MaxLevel"/> bracket the gear levels this tier can roll on:
/// a tier rolls when <c>MinLevel ≤ <see cref="ItemEntry.CraftingTargetLevel"/> ≤ MaxLevel</c>.
/// <see cref="MinRarity"/> gates by gear rarity; <see cref="SkillLevelPrereq"/> is the wearer
/// skill level required for the rolled buff to actually take effect once equipped.
/// </remarks>
public sealed record PowerTier(
    int Tier,
    IReadOnlyList<string> EffectDescs,
    int MaxLevel,
    int? MinLevel = null,
    string? MinRarity = null,
    int? SkillLevelPrereq = null);
