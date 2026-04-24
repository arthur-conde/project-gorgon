using System.Globalization;

namespace Gorgon.Shared.Reference;

/// <summary>
/// Projects the raw strings in <see cref="RecipeEntry.ResultEffects"/> into typed previews
/// that UI code can render. v1 only models <c>TSysCraftedEquipment(template[,tier[,subtype]])</c> —
/// the dominant prefix (~63% of all recipe effects). Other prefixes are ignored; callers should
/// treat an empty return list as "nothing to preview".
/// </summary>
public static class ResultEffectsParser
{
    private const string CraftedEquipmentPrefix = "TSysCraftedEquipment";

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="CraftedGearPreview"/> per
    /// well-formed <c>TSysCraftedEquipment</c> entry whose template resolves in
    /// <see cref="IReferenceDataService.ItemsByInternalName"/>. Malformed entries and
    /// unresolvable templates are skipped silently.
    /// </summary>
    public static IReadOnlyList<CraftedGearPreview> ParseCraftedGear(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<CraftedGearPreview>();
        foreach (var effect in effects)
        {
            if (TryParseCraftedGear(effect, refData, out var preview))
                previews.Add(preview);
        }
        return previews;
    }

    private static bool TryParseCraftedGear(string? effect, IReferenceDataService refData, out CraftedGearPreview preview)
    {
        preview = null!;
        if (string.IsNullOrWhiteSpace(effect)) return false;

        var openParen = effect.IndexOf('(');
        var closeParen = effect.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen) return false;

        var prefix = effect[..openParen];
        if (!prefix.Equals(CraftedEquipmentPrefix, StringComparison.Ordinal)) return false;

        var argsSpan = effect.AsSpan(openParen + 1, closeParen - openParen - 1);
        if (argsSpan.IsEmpty) return false;

        var args = argsSpan.ToString().Split(',');
        var internalName = args[0].Trim();
        if (internalName.Length == 0) return false;

        if (!refData.ItemsByInternalName.TryGetValue(internalName, out var item)) return false;

        int? tier = null;
        if (args.Length >= 2)
        {
            var tierToken = args[1].Trim();
            if (tierToken.Length > 0 && int.TryParse(tierToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                tier = parsed;
        }

        string? subtype = null;
        if (args.Length >= 3)
        {
            var subtypeToken = args[2].Trim();
            if (subtypeToken.Length > 0) subtype = subtypeToken;
        }

        preview = new CraftedGearPreview(internalName, item.Name, item.IconId, tier, subtype);
        return true;
    }
}
