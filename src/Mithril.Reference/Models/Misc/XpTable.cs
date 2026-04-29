using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>
/// One entry from <c>xptables.json</c>. <see cref="XpAmounts"/> is indexed
/// by skill level (level 1 = index 0, level 2 = index 1, etc.) and gives
/// the cumulative XP needed to reach that level.
/// </summary>
public sealed class XpTable
{
    public string? InternalName { get; set; }
    public IReadOnlyList<long>? XpAmounts { get; set; }
}
