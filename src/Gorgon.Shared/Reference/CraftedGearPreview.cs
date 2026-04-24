namespace Gorgon.Shared.Reference;

/// <summary>
/// Parsed projection of a <c>TSysCraftedEquipment</c> entry in <see cref="RecipeEntry.ResultEffects"/>.
/// The recipe authoring format is <c>TSysCraftedEquipment(templateInternalName[,tier[,subtype]])</c>;
/// the template always resolves to an <see cref="ItemEntry"/> in items.json, which is where
/// <see cref="DisplayName"/> and <see cref="IconId"/> come from.
/// </summary>
public sealed record CraftedGearPreview(
    string InternalName,
    string DisplayName,
    int IconId,
    int? Tier,
    string? Subtype)
{
    /// <summary>Single-line label used by both Celebrimbor and Elrond recipe tooltips.</summary>
    public string DisplayLine => (Tier, Subtype) switch
    {
        (null, null) => DisplayName,
        (int t, null) => $"{DisplayName} · Tier {t}",
        (null, string s) => $"{DisplayName} · {s}",
        (int t, string s) => $"{DisplayName} · Tier {t} · {s}",
    };
}
