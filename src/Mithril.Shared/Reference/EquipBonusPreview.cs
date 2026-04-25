namespace Mithril.Shared.Reference;

/// <summary>
/// Parsed projection of a <c>BoostItemEquipAdvancementTable(table)</c> entry in
/// <see cref="RecipeEntry.ResultEffects"/>. Equipping the crafted item passively
/// awards XP toward an advancement table — typically a <c>Foretold{Weapon}Damage</c>
/// or similar permanent-skill progression bucket.
/// <para>
/// <see cref="DisplayName"/> is a humanised form of <see cref="AdvancementTable"/>;
/// the parser keeps the raw token in case future UI wants to deep-link it.
/// </para>
/// </summary>
public sealed record EquipBonusPreview(
    string AdvancementTable,
    string DisplayName)
{
    public string DisplayLine => $"Equipping grants progress in {DisplayName}";
}
