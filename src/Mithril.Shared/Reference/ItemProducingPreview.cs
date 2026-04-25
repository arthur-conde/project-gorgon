namespace Mithril.Shared.Reference;

/// <summary>
/// Parsed projection of a "this recipe creates a specific in-game item" entry in
/// <see cref="RecipeEntry.ResultEffects"/>. Six prefix families flow through this
/// shared shape: <c>BrewItem</c>, <c>SummonPlant</c>, the Mining/Geology survey
/// creators, the regional treasure-map creators, <c>CreateNecroFuel</c>, and
/// <c>GiveNonMagicalLootProfile</c>.
/// <para>
/// <see cref="IconId"/> and <see cref="ResolvedItemInternalName"/> are populated
/// when the args reduce to an item that resolves in
/// <see cref="IReferenceDataService.ItemsByInternalName"/>; otherwise the
/// preview falls back to a humanised display name derived from the prefix args.
/// <see cref="Qualifier"/> carries the per-family decoration (e.g. <c>"Tier 4"</c>,
/// <c>"Mining Survey 1X"</c>, <c>"Eltibule · Poor"</c>) that the chip should show
/// alongside the item name.
/// </para>
/// </summary>
public sealed record ItemProducingPreview(
    string DisplayName,
    int? IconId,
    string? Qualifier,
    string? ResolvedItemInternalName)
{
    public string DisplayLine => string.IsNullOrEmpty(Qualifier)
        ? DisplayName
        : $"{DisplayName} ({Qualifier})";
}
