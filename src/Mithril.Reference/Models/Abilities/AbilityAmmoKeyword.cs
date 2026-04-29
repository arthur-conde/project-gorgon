namespace Mithril.Reference.Models.Abilities;

/// <summary>
/// One entry in an ability's <c>AmmoKeywords</c> list — keyword-matched
/// ammo requirement with a count.
/// </summary>
public sealed class AbilityAmmoKeyword
{
    public string? ItemKeyword { get; set; }
    public int Count { get; set; }
}
