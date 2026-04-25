namespace Mithril.Shared.Reference;

/// <summary>
/// Parsed projection of an <c>AddItemTSysPower(powerInternalName, tier)</c> entry in
/// <see cref="RecipeEntry.ResultEffects"/>. The power resolves to a <see cref="PowerEntry"/>
/// in tsysclientinfo.json; this record carries the pre-rendered effect lines for the
/// requested tier so UI code can bind directly without redoing the render pipeline.
/// </summary>
/// <remarks>
/// <see cref="Suffix"/> is nullable because only drop/loot powers carry one ("of Archery"
/// on <c>ArcheryBoost</c>); deterministic infusion powers (<c>ShamanicHeadArmor</c>, etc.)
/// typically omit it and fall back to <see cref="PowerInternalName"/> for display.
/// </remarks>
public sealed record AugmentPreview(
    string PowerInternalName,
    string? Suffix,
    int Tier,
    IReadOnlyList<EffectLine> EffectLines)
{
    /// <summary>Single-line label used by recipe tooltips and the Augmentation section header.</summary>
    public string DisplayLine => Suffix is { Length: > 0 }
        ? $"{Suffix} · Tier {Tier}"
        : $"{PowerInternalName} · Tier {Tier}";
}
