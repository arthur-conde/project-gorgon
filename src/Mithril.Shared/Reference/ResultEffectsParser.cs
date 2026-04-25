using System.Globalization;

namespace Mithril.Shared.Reference;

/// <summary>
/// Projects the raw strings in <see cref="RecipeEntry.ResultEffects"/> into typed previews
/// that UI code can render. Currently models two prefixes:
/// <list type="bullet">
///   <item><c>TSysCraftedEquipment(template[,tier[,subtype]])</c> — ~63% of effects, the
///   deterministic crafted-gear case (see <see cref="ParseCraftedGear"/>).</item>
///   <item><c>AddItemTSysPower(power,tier)</c> — ~4% of effects, augmentation recipes that
///   attach a specific tier of a tsysclientinfo power to the input item (see
///   <see cref="ParseAugments"/>).</item>
/// </list>
/// Other prefixes (<c>BestowRecipeIfNotKnown</c>, calligraphy, <c>ExtractTSysPower</c> pools)
/// are ignored; callers treat an empty return list as "nothing to preview".
/// </summary>
public static class ResultEffectsParser
{
    private const string CraftedEquipmentPrefix = "TSysCraftedEquipment";
    private const string AddPowerPrefix = "AddItemTSysPower";

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

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="AugmentPreview"/> per
    /// well-formed <c>AddItemTSysPower(power,tier)</c> entry whose power resolves in
    /// <see cref="IReferenceDataService.Powers"/> and whose tier exists on that power.
    /// Effect descriptions are pre-rendered through <see cref="EffectDescsRenderer"/> so
    /// callers can bind directly. Malformed entries are skipped silently.
    /// </summary>
    /// <remarks>
    /// Pool-based prefixes (<c>ExtractTSysPower(slot, poolKey, minTier, maxTier)</c> and
    /// Enchanted <c>TSysCraftedEquipment</c> variants) need a different preview shape
    /// (a <em>set</em> of possible rolls with filter context) and are deliberately
    /// deferred to a future phase — extend this parser with a sibling <c>ParseAugmentPools</c>
    /// when those land.
    /// </remarks>
    public static IReadOnlyList<AugmentPreview> ParseAugments(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<AugmentPreview>();
        foreach (var effect in effects)
        {
            if (TryParseAugment(effect, refData, out var preview))
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

    private static bool TryParseAugment(string? effect, IReferenceDataService refData, out AugmentPreview preview)
    {
        preview = null!;
        if (string.IsNullOrWhiteSpace(effect)) return false;

        var openParen = effect.IndexOf('(');
        var closeParen = effect.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen) return false;

        var prefix = effect[..openParen];
        if (!prefix.Equals(AddPowerPrefix, StringComparison.Ordinal)) return false;

        var argsSpan = effect.AsSpan(openParen + 1, closeParen - openParen - 1);
        if (argsSpan.IsEmpty) return false;

        var args = argsSpan.ToString().Split(',');
        if (args.Length < 2) return false;

        var powerName = args[0].Trim();
        if (powerName.Length == 0) return false;

        var tierToken = args[1].Trim();
        if (!int.TryParse(tierToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tier)) return false;

        if (!refData.Powers.TryGetValue(powerName, out var power)) return false;
        if (!power.Tiers.TryGetValue(tier, out var tierEntry)) return false;

        var lines = EffectDescsRenderer.Render(tierEntry.EffectDescs, refData.Attributes);
        preview = new AugmentPreview(power.InternalName, power.Suffix, tier, lines);
        return true;
    }
}
