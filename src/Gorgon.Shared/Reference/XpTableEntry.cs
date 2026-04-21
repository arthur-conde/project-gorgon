namespace Gorgon.Shared.Reference;

/// <summary>
/// One XP table from xptables.json. <see cref="XpAmounts"/> is indexed by
/// (level - 1), so <c>XpAmounts[0]</c> is XP required from level 0 to level 1.
/// </summary>
public sealed record XpTableEntry(string InternalName, IReadOnlyList<long> XpAmounts);
