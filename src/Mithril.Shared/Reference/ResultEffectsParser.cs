using System.Globalization;

namespace Mithril.Shared.Reference;

/// <summary>
/// Projects the raw strings in <see cref="RecipeEntry.ResultEffects"/> into typed previews
/// that UI code can render. Models the prefixes documented under <c>BundledData/INDEX.md</c>:
/// <list type="bullet">
///   <item><c>TSysCraftedEquipment(template[,tier[,subtype]])</c>, <c>GiveTSysItem(template)</c>,
///   <c>CraftSimpleTSysItem(template)</c> — the deterministic crafted-gear case
///   (<see cref="ParseCraftedGear"/>).</item>
///   <item><c>AddItemTSysPower(power,tier)</c> — augmentation recipes that attach a specific
///   tier of a <c>tsysclientinfo</c> power to the input item (<see cref="ParseAugments"/>).</item>
///   <item><c>BestowRecipeIfNotKnown(recipe)</c> — recipe-teaching crafts (<see cref="ParseTaughtRecipes"/>).</item>
///   <item><c>CraftWaxItem(wax,power,tier,durability)</c> — wax/tuneup-kit crafts (<see cref="ParseWaxItems"/>).</item>
///   <item><c>AddItemTSysPowerWax(power,tier,durability)</c> — finite-use augmentations
///   (<see cref="ParseAddItemTSysPowerWaxes"/>).</item>
///   <item><c>ExtractTSysPower(augmentItem,skill,minTier,maxTier)</c> and the enchantment
///   form of <c>TSysCraftedEquipment</c> — pool-based rolls (<see cref="ParseAugmentPools"/>).</item>
///   <item>Calligraphy / meditation / TSys-augment behavioural tags
///   (<see cref="ParseEffectTags"/>) — <c>ApplyAugmentOil</c>,
///   <c>RemoveAddedTSysPowerFromItem</c>, <c>ApplyAddItemTSysPowerWaxFromSourceItem</c>,
///   and the per-slot <c>Decompose*ItemIntoAugmentResources</c> variants.</item>
/// </list>
/// All methods skip malformed/unresolvable entries silently; callers treat an empty list as
/// "nothing to preview".
/// </summary>
public static class ResultEffectsParser
{
    // Crafted gear (single 1-arg template-name shape, plus the multi-arg TSysCraftedEquipment variant).
    private const string CraftedEquipmentPrefix = "TSysCraftedEquipment";
    private const string GiveTSysItemPrefix = "GiveTSysItem";
    private const string CraftSimpleTSysItemPrefix = "CraftSimpleTSysItem";

    // Augmentation prefixes.
    private const string AddPowerPrefix = "AddItemTSysPower";

    // Phase 7 prefixes.
    private const string BestowRecipePrefix = "BestowRecipeIfNotKnown";
    private const string CraftWaxItemPrefix = "CraftWaxItem";
    private const string ExtractTSysPowerPrefix = "ExtractTSysPower";

    // TSys-augment family — finite-use application sibling of AddItemTSysPower.
    private const string AddItemTSysPowerWaxPrefix = "AddItemTSysPowerWax";

    // Knowledge / progression prefixes.
    private const string ResearchPrefix = "Research";
    private const string GivePrefix = "Give";
    private const string XpSuffix = "Xp";
    private const string DiscoverWordOfPowerPrefix = "DiscoverWordOfPower";
    private const string LearnAbilityPrefix = "LearnAbility";

    // Item-producing prefixes.
    private const string BrewItemPrefix = "BrewItem";
    private const string SummonPlantPrefix = "SummonPlant";
    private const string CreateMiningSurveyPrefix = "CreateMiningSurvey";
    private const string CreateGeologySurveyPrefix = "CreateGeologySurvey";
    private const string CreateTreasureMapPrefix = "Create";
    private const string TreasureMapInfix = "TreasureMap";
    private const string CreateNecroFuelTag = "CreateNecroFuel";
    private const string GiveNonMagicalLootProfilePrefix = "GiveNonMagicalLootProfile";

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="CraftedGearPreview"/> per
    /// well-formed <c>TSysCraftedEquipment</c> / <c>GiveTSysItem</c> / <c>CraftSimpleTSysItem</c>
    /// entry whose template resolves in <see cref="IReferenceDataService.ItemsByInternalName"/>.
    /// Malformed entries and unresolvable templates are skipped silently.
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
    /// Effect descriptions are pre-rendered through <see cref="EffectDescsRenderer"/>.
    /// </summary>
    /// <remarks>
    /// Slot validation against <c>PowerEntry.Slots</c> is intentionally not performed
    /// here. <c>AddItemTSysPower</c> recipes don't bind a target item at parse time —
    /// the target is the recipe's input ingredient at craft time — so we couldn't
    /// determine the gear slot to validate against. In practice mismatches in shipped
    /// data are rare; the augment pool viewer is where the real slot gate lives.
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

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="TaughtRecipePreview"/> per
    /// well-formed <c>BestowRecipeIfNotKnown(recipe)</c> entry whose recipe resolves in
    /// <see cref="IReferenceDataService.RecipesByInternalName"/>.
    /// </summary>
    public static IReadOnlyList<TaughtRecipePreview> ParseTaughtRecipes(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<TaughtRecipePreview>();
        foreach (var effect in effects)
        {
            if (TryParseTaughtRecipe(effect, refData, out var preview))
                previews.Add(preview);
        }
        return previews;
    }

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="WaxItemPreview"/> per
    /// well-formed <c>CraftWaxItem(waxItem,power,tier,durability)</c> entry whose power
    /// resolves and whose tier exists. Effect descriptions are pre-rendered via
    /// <see cref="EffectDescsRenderer"/>.
    /// </summary>
    public static IReadOnlyList<WaxItemPreview> ParseWaxItems(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<WaxItemPreview>();
        foreach (var effect in effects)
        {
            if (TryParseWaxItem(effect, refData, out var preview))
                previews.Add(preview);
        }
        return previews;
    }

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="WaxAugmentPreview"/> per
    /// well-formed <c>AddItemTSysPowerWax(power,tier,durability)</c> entry whose power
    /// resolves and whose tier exists. Sibling of <see cref="ParseAugments"/> for
    /// finite-use applications: the recipe attaches a power tier to a target item that
    /// wears off after <c>durability</c> uses, instead of producing a wax item template.
    /// </summary>
    public static IReadOnlyList<WaxAugmentPreview> ParseAddItemTSysPowerWaxes(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<WaxAugmentPreview>();
        foreach (var effect in effects)
        {
            if (TryParseAddItemTSysPowerWax(effect, refData, out var preview))
                previews.Add(preview);
        }
        return previews;
    }

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="AugmentPoolPreview"/> per
    /// pool-based prefix: <c>ExtractTSysPower(augmentItem,skill,minTier,maxTier)</c> and the
    /// enchantment-source form of <c>TSysCraftedEquipment</c>. Each preview is a lightweight
    /// navigation token; the full per-power option list is materialized lazily by the pool
    /// viewer when the user clicks "Browse pool".
    /// </summary>
    /// <remarks>
    /// Note: this method emits a pool token alongside any <see cref="CraftedGearPreview"/>
    /// already produced by <see cref="ParseCraftedGear"/> for the same
    /// <c>TSysCraftedEquipment</c> entry. The chip preview and the pool preview are
    /// independent; both are surfaced when the underlying template has a non-empty
    /// <see cref="ItemEntry.TSysProfile"/>.
    /// </remarks>
    public static IReadOnlyList<AugmentPoolPreview> ParseAugmentPools(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<AugmentPoolPreview>();
        foreach (var effect in effects)
        {
            if (TryParseAugmentPool(effect, refData, out var preview))
                previews.Add(preview);
        }
        return previews;
    }

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="EffectTagPreview"/> per
    /// recognized zero/one-arg tag prefix. Coverage today:
    /// <list type="bullet">
    ///   <item>Calligraphy: <c>DispelCalligraphyA/B/C</c>, <c>CalligraphyComboNN[L]</c>
    ///   (optional letter-suffix variant), <c>Calligraphy{N}{Slot}</c>, and the
    ///   typed sub-families <c>CalligraphySlash/Rage/FirstAid/ArmorRepair/Piercing/SlashingFlat{N}</c>.</item>
    ///   <item>Meditation: <c>MeditationWithDaily[(combo)]</c>, <c>MeditationNoDaily</c>,
    ///   and the suffixed-tier families <c>MeditationHealth/Power/Breath/CritDmg/Indirect/
    ///   BodyHeat/Metabolism/DeathAvoidance/BuffIndirectCold/VulnPsi/VulnFire/VulnCold/
    ///   VulnDarkness/VulnNature/VulnElectricity{N}</c>.</item>
    ///   <item>Whittling: <c>Whittling{N}</c>, <c>WhittlingKnifeBuff{N}</c>.</item>
    ///   <item>Augury: <c>Augury{N}</c>.</item>
    ///   <item>Premonition: <c>SpawnPremonition_{kind}</c>, <c>DispelSpawnPremonitionsOnDeath</c>.</item>
    ///   <item>Status: <c>Infertility</c>, <c>SleepResistance</c>, <c>SexualEnergy</c>,
    ///   <c>ArgumentResistance</c>.</item>
    ///   <item>TempestEnergy: <c>PermanentlyRaiseMaxTempestEnergy(N)</c> — parametrised.</item>
    ///   <item>TSys-augment behavioural tags: <c>ApplyAugmentOil</c>,
    ///   <c>RemoveAddedTSysPowerFromItem</c>, <c>ApplyAddItemTSysPowerWaxFromSourceItem</c>.</item>
    ///   <item>Per-slot <c>Decompose*ItemIntoAugmentResources</c> variants.</item>
    /// </list>
    /// Unknown prefixes are deliberately not emitted; the generic-fallback
    /// future-proofing path lives in a later parser phase.
    /// </summary>
    public static IReadOnlyList<EffectTagPreview> ParseEffectTags(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<EffectTagPreview>();
        foreach (var effect in effects)
        {
            if (TryParseEffectTag(effect, out var preview))
                previews.Add(preview);
        }
        return previews;
    }

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="ResearchProgressPreview"/>
    /// per well-formed <c>Research{Topic}{Level}</c> entry. The trailing integer is
    /// stripped greedily; the remainder becomes the topic. Topics observed in shipped
    /// recipes.json: <c>WeatherWitching</c>, <c>FireMagic</c>, <c>IceMagic</c>,
    /// <c>ExoticFireWalls</c>.
    /// </summary>
    public static IReadOnlyList<ResearchProgressPreview> ParseResearchProgress(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<ResearchProgressPreview>();
        foreach (var effect in effects)
        {
            if (TryParseResearchProgress(effect, out var preview))
                previews.Add(preview);
        }
        return previews;
    }

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="XpGrantPreview"/> per
    /// well-formed <c>Give{Skill}Xp</c> entry. Today only <c>GiveTeleportationXp</c>
    /// ships, but the typed shape lets future <c>Give*Xp</c> prefixes flow through
    /// without parser churn.
    /// </summary>
    public static IReadOnlyList<XpGrantPreview> ParseXpGrants(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<XpGrantPreview>();
        foreach (var effect in effects)
        {
            if (TryParseXpGrant(effect, out var preview))
                previews.Add(preview);
        }
        return previews;
    }

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="WordOfPowerPreview"/>
    /// per well-formed <c>DiscoverWordOfPower{N}</c> entry.
    /// </summary>
    public static IReadOnlyList<WordOfPowerPreview> ParseWordsOfPower(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<WordOfPowerPreview>();
        foreach (var effect in effects)
        {
            if (TryParseWordOfPower(effect, out var preview))
                previews.Add(preview);
        }
        return previews;
    }

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="LearnedAbilityPreview"/>
    /// per well-formed <c>LearnAbility(internalName)</c> entry. No abilities lookup
    /// table exists today, so <see cref="LearnedAbilityPreview.DisplayName"/> falls
    /// back to a humanised form of the internal name.
    /// </summary>
    public static IReadOnlyList<LearnedAbilityPreview> ParseLearnedAbilities(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<LearnedAbilityPreview>();
        foreach (var effect in effects)
        {
            if (TryParseLearnedAbility(effect, out var preview))
                previews.Add(preview);
        }
        return previews;
    }

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="ItemProducingPreview"/>
    /// per recognised item-producing prefix. Six families flow through this method:
    /// <list type="bullet">
    ///   <item><c>BrewItem(tier,skillLvl,recipeArgs)</c> — generic "brewed item" chip
    ///   with the tier as qualifier (the produced item is the recipe's own output;
    ///   the args here describe ingredients + effects, not a target item).</item>
    ///   <item><c>SummonPlant(type,_,itemName~scalars)</c> — resolves <c>itemName</c>
    ///   in <see cref="IReferenceDataService.ItemsByInternalName"/>.</item>
    ///   <item><c>CreateMiningSurvey{N}[X|Y](itemName)</c> — survey items, with the
    ///   tier+modifier suffix preserved as the qualifier.</item>
    ///   <item><c>CreateGeologySurvey{Color}(itemName)</c> — colour-keyed surveys.</item>
    ///   <item><c>Create{Region}TreasureMap{Quality}</c> — zero-arg; we attempt to
    ///   resolve the item via the <c>TreasureMap{Region}{Quality}</c> reordering and
    ///   fall back to a humanised label.</item>
    ///   <item><c>CreateNecroFuel</c> (zero-arg) and
    ///   <c>GiveNonMagicalLootProfile(profile)</c> (1-arg).</item>
    /// </list>
    /// Each preview falls back to a humanised display name when the args don't
    /// resolve to a known <see cref="ItemEntry"/>; the icon is only populated on
    /// successful resolution.
    /// </summary>
    public static IReadOnlyList<ItemProducingPreview> ParseItemProducing(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<ItemProducingPreview>();
        foreach (var effect in effects)
        {
            if (TryParseItemProducing(effect, refData, out var preview))
                previews.Add(preview);
        }
        return previews;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static bool TryParseCraftedGear(string? effect, IReferenceDataService refData, out CraftedGearPreview preview)
    {
        preview = null!;
        if (!TryParsePrefixCall(effect, out var prefix, out var args)) return false;

        var isCraftedEquipment = prefix.Equals(CraftedEquipmentPrefix, StringComparison.Ordinal);
        var isGiveOrSimple = prefix.Equals(GiveTSysItemPrefix, StringComparison.Ordinal)
                          || prefix.Equals(CraftSimpleTSysItemPrefix, StringComparison.Ordinal);
        if (!isCraftedEquipment && !isGiveOrSimple) return false;

        if (args.Length == 0) return false;
        var internalName = args[0].Trim();
        if (internalName.Length == 0) return false;
        if (!refData.ItemsByInternalName.TryGetValue(internalName, out var item)) return false;

        // GiveTSysItem / CraftSimpleTSysItem only carry the template name; tier/subtype stay null.
        if (isGiveOrSimple)
        {
            preview = new CraftedGearPreview(internalName, item.Name, item.IconId, null, null);
            return true;
        }

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
        if (!TryParsePrefixCall(effect, out var prefix, out var args)) return false;
        if (!prefix.Equals(AddPowerPrefix, StringComparison.Ordinal)) return false;
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

    private static bool TryParseTaughtRecipe(string? effect, IReferenceDataService refData, out TaughtRecipePreview preview)
    {
        preview = null!;
        if (!TryParsePrefixCall(effect, out var prefix, out var args)) return false;
        if (!prefix.Equals(BestowRecipePrefix, StringComparison.Ordinal)) return false;
        if (args.Length == 0) return false;

        var recipeInternalName = args[0].Trim();
        if (recipeInternalName.Length == 0) return false;
        if (!refData.RecipesByInternalName.TryGetValue(recipeInternalName, out var recipe)) return false;

        var displayName = string.IsNullOrEmpty(recipe.Name) ? recipeInternalName : recipe.Name;
        preview = new TaughtRecipePreview(recipeInternalName, displayName, recipe.Skill, recipe.SkillLevelReq);
        return true;
    }

    private static bool TryParseWaxItem(string? effect, IReferenceDataService refData, out WaxItemPreview preview)
    {
        preview = null!;
        if (!TryParsePrefixCall(effect, out var prefix, out var args)) return false;
        if (!prefix.Equals(CraftWaxItemPrefix, StringComparison.Ordinal)) return false;
        if (args.Length < 4) return false;

        var waxItem = args[0].Trim();
        var powerName = args[1].Trim();
        var tierToken = args[2].Trim();
        var durabilityToken = args[3].Trim();
        if (waxItem.Length == 0 || powerName.Length == 0) return false;
        if (!int.TryParse(tierToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tier)) return false;
        if (!int.TryParse(durabilityToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var durability)) return false;

        if (!refData.Powers.TryGetValue(powerName, out var power)) return false;
        if (!power.Tiers.TryGetValue(tier, out var tierEntry)) return false;

        var lines = EffectDescsRenderer.Render(tierEntry.EffectDescs, refData.Attributes);
        preview = new WaxItemPreview(waxItem, power.InternalName, power.Suffix, tier, durability, lines);
        return true;
    }

    private static bool TryParseAddItemTSysPowerWax(string? effect, IReferenceDataService refData, out WaxAugmentPreview preview)
    {
        preview = null!;
        if (!TryParsePrefixCall(effect, out var prefix, out var args)) return false;
        if (!prefix.Equals(AddItemTSysPowerWaxPrefix, StringComparison.Ordinal)) return false;
        if (args.Length < 3) return false;

        var powerName = args[0].Trim();
        var tierToken = args[1].Trim();
        var durabilityToken = args[2].Trim();
        if (powerName.Length == 0) return false;
        if (!int.TryParse(tierToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tier)) return false;
        if (!int.TryParse(durabilityToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var durability)) return false;

        if (!refData.Powers.TryGetValue(powerName, out var power)) return false;
        if (!power.Tiers.TryGetValue(tier, out var tierEntry)) return false;

        var lines = EffectDescsRenderer.Render(tierEntry.EffectDescs, refData.Attributes);
        preview = new WaxAugmentPreview(power.InternalName, power.Suffix, tier, durability, lines);
        return true;
    }

    private static bool TryParseAugmentPool(string? effect, IReferenceDataService refData, out AugmentPoolPreview preview)
    {
        preview = null!;
        if (!TryParsePrefixCall(effect, out var prefix, out var args)) return false;

        if (prefix.Equals(ExtractTSysPowerPrefix, StringComparison.Ordinal))
            return TryBuildExtractPool(args, refData, out preview);

        if (prefix.Equals(CraftedEquipmentPrefix, StringComparison.Ordinal))
            return TryBuildCraftedEquipmentPool(args, refData, out preview);

        return false;
    }

    private static bool TryBuildExtractPool(string[] args, IReferenceDataService refData, out AugmentPoolPreview preview)
    {
        preview = null!;
        if (args.Length < 4) return false;

        var augmentItemName = args[0].Trim();
        var minTierToken = args[2].Trim();
        var maxTierToken = args[3].Trim();
        if (augmentItemName.Length == 0) return false;
        if (!int.TryParse(minTierToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minTier)) return false;
        if (!int.TryParse(maxTierToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxTier)) return false;

        if (!refData.ItemsByInternalName.TryGetValue(augmentItemName, out var augmentItem)) return false;
        var profileName = augmentItem.TSysProfile;
        if (string.IsNullOrEmpty(profileName)) return false;
        if (!refData.Profiles.TryGetValue(profileName, out var poolPowers)) return false;

        var optionCount = CountEligiblePowers(poolPowers, refData, minTier, maxTier);
        if (optionCount == 0) return false;

        // Extract recipes don't encode a power-skill gate in their args (arg2 is the
        // *crafting* skill, not the rolled-power skill). The roll is unconstrained
        // within the profile, so leave RecommendedSkill null. Slot filtering is also
        // intentionally skipped: an extraction recipe doesn't bind a target item, so
        // PowerEntry.Slots can't be applied here — the slot gate would have to fire
        // at "apply augment to item X" time, which isn't a UI affordance today.
        var sourceLabel = $"Extractions from {augmentItem.Name} (Level {minTier}-{maxTier})";
        preview = new AugmentPoolPreview(sourceLabel, profileName, minTier, maxTier, optionCount,
            RecommendedSkill: null,
            CraftingTargetLevel: augmentItem.CraftingTargetLevel);
        return true;
    }

    private static bool TryBuildCraftedEquipmentPool(string[] args, IReferenceDataService refData, out AugmentPoolPreview preview)
    {
        preview = null!;
        if (args.Length == 0) return false;

        var templateName = args[0].Trim();
        if (templateName.Length == 0) return false;
        if (!refData.ItemsByInternalName.TryGetValue(templateName, out var template)) return false;

        var profileName = template.TSysProfile;
        if (string.IsNullOrEmpty(profileName)) return false;
        if (!refData.Profiles.TryGetValue(profileName, out var poolPowers)) return false;
        if (poolPowers.Count == 0) return false;

        // arg3 is the form/skill gate the treasure system applies to the roll
        // ("Werewolf", "Cow", "Deer", etc.). Its absence means the roll is
        // unconstrained within the profile — don't fabricate a constraint from
        // the wearer's SkillReqs, since those describe equip eligibility, not
        // the roll-time gate.
        string? recommendedSkill = null;
        if (args.Length >= 3)
        {
            var subtype = args[2].Trim();
            if (subtype.Length > 0) recommendedSkill = subtype;
        }

        // arg2 is the rarity-floor bump: a base enchant rolls at Uncommon (rank 1);
        // Max-Enchanting (arg2=1) rolls at Rare (rank 2). Common is reserved for
        // non-enchanted base crafts and never appears in tsysclientinfo's MinRarity
        // gates. Rank 0 means "no rarity constraint surfaced".
        int? rolledRarityRank = null;
        if (args.Length >= 2 && int.TryParse(args[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rarityBump))
            rolledRarityRank = 1 + rarityBump; // 0 → Uncommon (1), 1 → Rare (2)
        else
            rolledRarityRank = 1; // Implicit base enchant = Uncommon floor.

        // Count distinct powers eligible to roll, not (power × tier) pairs. A real
        // enchant picks ONE power from the pool; the tier is set by the craft, not
        // rolled separately — so this is the player-meaningful "outcomes per craft"
        // number. The pool viewer flattens tiers for inspection; that's a separate
        // axis labeled distinctly in the UI.
        //
        // PowerEntry.Slots constrains which gear slots a power can roll on
        // (e.g. ParryRiposteBoostTrauma is Slots: [MainHand, Ring], so it never
        // rolls on a chest piece). Filter the pool by template.EquipSlot when set;
        // verified against tsysclientinfo.json that no power has empty Slots, so
        // there's no "empty = unrestricted" fallback to handle.
        var optionCount = poolPowers.Count;
        if (!string.IsNullOrEmpty(template.EquipSlot))
        {
            optionCount = CountPowersWithSlot(poolPowers, refData, template.EquipSlot);
        }

        var sourceLabel = $"Possible rolls for {template.Name}";
        preview = new AugmentPoolPreview(sourceLabel, profileName, null, null, optionCount,
            RecommendedSkill: recommendedSkill,
            CraftingTargetLevel: template.CraftingTargetLevel,
            RolledRarityRank: rolledRarityRank,
            SourceEquipSlot: template.EquipSlot);
        return true;
    }

    private static int CountPowersWithSlot(
        IReadOnlyList<string> powerNames, IReferenceDataService refData, string slot)
    {
        var count = 0;
        foreach (var powerName in powerNames)
        {
            if (!refData.Powers.TryGetValue(powerName, out var power)) continue;
            foreach (var s in power.Slots)
            {
                if (string.Equals(s, slot, StringComparison.Ordinal))
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Counts distinct powers in the profile that have at least one tier in
    /// <c>[minTier, maxTier]</c> (inclusive). Each power is a single roll outcome —
    /// the game picks one power per extraction; the tier reflects the source augment.
    /// </summary>
    private static int CountEligiblePowers(
        IReadOnlyList<string> powerNames, IReferenceDataService refData, int minTier, int maxTier)
    {
        var count = 0;
        foreach (var powerName in powerNames)
        {
            if (!refData.Powers.TryGetValue(powerName, out var power)) continue;
            foreach (var (tierNum, _) in power.Tiers)
            {
                if (tierNum >= minTier && tierNum <= maxTier)
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Suffixed-tier tag families: prefix + integer suffix → <c>"{format} Tier {N}"</c>-style line.
    /// Listed prefix-longest-first so <c>MeditationVulnPsi</c> wins over a hypothetical <c>Meditation</c>.
    /// </summary>
    private static readonly (string Prefix, string Format)[] SuffixedTierFamilies =
    {
        ("MeditationVulnElectricity", "Meditation: Electricity Vulnerability Tier {0}"),
        ("MeditationVulnDarkness", "Meditation: Darkness Vulnerability Tier {0}"),
        ("MeditationBuffIndirectCold", "Meditation: Indirect Cold Buff Tier {0}"),
        ("MeditationDeathAvoidance", "Meditation: Death Avoidance Tier {0}"),
        ("MeditationVulnNature", "Meditation: Nature Vulnerability Tier {0}"),
        ("MeditationMetabolism", "Meditation: Metabolism Tier {0}"),
        ("MeditationVulnCold", "Meditation: Cold Vulnerability Tier {0}"),
        ("MeditationVulnFire", "Meditation: Fire Vulnerability Tier {0}"),
        ("MeditationVulnPsi", "Meditation: Psychic Vulnerability Tier {0}"),
        ("MeditationCritDmg", "Meditation: Crit Damage Tier {0}"),
        ("MeditationIndirect", "Meditation: Indirect Damage Tier {0}"),
        ("MeditationBodyHeat", "Meditation: Body Heat Tier {0}"),
        ("MeditationHealth", "Meditation: Health Tier {0}"),
        ("MeditationBreath", "Meditation: Breath Tier {0}"),
        ("MeditationPower", "Meditation: Power Tier {0}"),
        ("CalligraphyArmorRepair", "Calligraphy: Armor Repair Tier {0}"),
        ("CalligraphySlashingFlat", "Calligraphy: Slashing Flat Tier {0}"),
        ("CalligraphyFirstAid", "Calligraphy: First Aid Tier {0}"),
        ("CalligraphyPiercing", "Calligraphy: Piercing Tier {0}"),
        ("CalligraphySlash", "Calligraphy: Slashing Tier {0}"),
        ("CalligraphyRage", "Calligraphy: Rage Tier {0}"),
        ("WhittlingKnifeBuff", "Whittling Knife Buff Tier {0}"),
        ("Whittling", "Whittling Tier {0}"),
        ("Augury", "Augury Tier {0}"),
    };

    /// <summary>
    /// Zero-arg tags that map to a single fixed display line. Order doesn't matter
    /// (exact match), kept alphabetical for readability.
    /// </summary>
    private static readonly Dictionary<string, string> ExactTagLines = new(StringComparer.Ordinal)
    {
        ["ApplyAddItemTSysPowerWaxFromSourceItem"] = "Applies augment wax from source item",
        ["ApplyAugmentOil"] = "Applies augment oil",
        ["ArgumentResistance"] = "Argument Resistance",
        ["DispelSpawnPremonitionsOnDeath"] = "Dispels premonitions on death",
        ["Infertility"] = "Infertility",
        ["MeditationNoDaily"] = "Meditation: No Daily",
        ["RemoveAddedTSysPowerFromItem"] = "Removes augment from item",
        ["SexualEnergy"] = "Sexual Energy",
        ["SleepResistance"] = "Sleep Resistance",
    };

    private static bool TryParseEffectTag(string? effect, out EffectTagPreview preview)
    {
        preview = null!;
        if (string.IsNullOrWhiteSpace(effect)) return false;
        var trimmed = effect.Trim();

        // Exact-match zero-arg tags.
        if (ExactTagLines.TryGetValue(trimmed, out var line))
        {
            preview = new EffectTagPreview(line);
            return true;
        }

        // Zero-arg form: DispelCalligraphyA / DispelCalligraphyB / DispelCalligraphyC.
        if (trimmed.StartsWith("DispelCalligraphy", StringComparison.Ordinal) && trimmed.Length == "DispelCalligraphy".Length + 1)
        {
            var slot = trimmed[^1];
            if (slot is 'A' or 'B' or 'C')
            {
                preview = new EffectTagPreview($"Calligraphy Slot {slot}");
                return true;
            }
            return false;
        }

        // Zero-arg form: CalligraphyComboNN (digit suffix), with optional trailing
        // letter (CalligraphyCombo1C..CalligraphyCombo7C). The letter denotes which
        // calligraphy slot the combo belongs to; surfacing it keeps the chip
        // unambiguous when multiple slots share the same number.
        if (trimmed.StartsWith("CalligraphyCombo", StringComparison.Ordinal))
        {
            var suffix = trimmed["CalligraphyCombo".Length..];
            char? letter = null;
            if (suffix.Length > 0 && char.IsLetter(suffix[^1]))
            {
                letter = suffix[^1];
                suffix = suffix[..^1];
            }
            if (suffix.Length > 0 && int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                preview = new EffectTagPreview(letter is null
                    ? $"Combo: Calligraphy Combo {n}"
                    : $"Combo: Calligraphy Combo {n} (Slot {letter})");
                return true;
            }
            return false;
        }

        // Optional-arg form: MeditationWithDaily or MeditationWithDaily(combo).
        if (trimmed.StartsWith("MeditationWithDaily", StringComparison.Ordinal))
        {
            if (trimmed.Length == "MeditationWithDaily".Length)
            {
                preview = new EffectTagPreview("Grants: Daily Meditation Combo");
                return true;
            }

            if (TryParsePrefixCall(trimmed, out var prefix, out var args)
                && prefix.Equals("MeditationWithDaily", StringComparison.Ordinal)
                && args.Length >= 1)
            {
                var combo = args[0].Trim();
                if (combo.Length > 0)
                {
                    preview = new EffectTagPreview($"Grants: Daily Meditation Combo — {Humanize(combo)}");
                    return true;
                }
            }
            return false;
        }

        // Calligraphy{N}{Slot} — e.g. Calligraphy1B, Calligraphy15B, Calligraphy5D.
        // Distinct from the CalligraphySlash/Rage/etc. families above (which start
        // with a letter token, not a digit), so we test for it after the families.
        // Match this BEFORE the generic suffixed-tier loop because "Calligraphy"
        // isn't in that table — but a future addition there shouldn't shadow it.
        if (trimmed.StartsWith("Calligraphy", StringComparison.Ordinal)
            && trimmed.Length > "Calligraphy".Length
            && char.IsDigit(trimmed["Calligraphy".Length]))
        {
            var rest = trimmed["Calligraphy".Length..];
            char? slot = null;
            if (rest.Length > 0 && char.IsLetter(rest[^1]))
            {
                slot = rest[^1];
                rest = rest[..^1];
            }
            if (rest.Length > 0 && int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
            {
                preview = new EffectTagPreview(slot is null
                    ? $"Calligraphy {num}"
                    : $"Calligraphy {num} Slot {slot}");
                return true;
            }
            return false;
        }

        // Suffixed-tier families — strip prefix, parse trailing integer.
        foreach (var (prefix, format) in SuffixedTierFamilies)
        {
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var suffix = trimmed[prefix.Length..];
            if (suffix.Length == 0) continue;
            if (!int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tier)) continue;
            preview = new EffectTagPreview(string.Format(CultureInfo.InvariantCulture, format, tier));
            return true;
        }

        // SpawnPremonition_{kind} — humanise the kind suffix.
        if (trimmed.StartsWith("SpawnPremonition_", StringComparison.Ordinal)
            && trimmed.Length > "SpawnPremonition_".Length)
        {
            var kind = trimmed["SpawnPremonition_".Length..];
            preview = new EffectTagPreview($"Premonition: {Humanize(kind)}");
            return true;
        }

        // Parametrised: PermanentlyRaiseMaxTempestEnergy(N).
        if (trimmed.StartsWith("PermanentlyRaiseMaxTempestEnergy", StringComparison.Ordinal)
            && TryParsePrefixCall(trimmed, out var tePrefix, out var teArgs)
            && tePrefix.Equals("PermanentlyRaiseMaxTempestEnergy", StringComparison.Ordinal)
            && teArgs.Length >= 1
            && int.TryParse(teArgs[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var teDelta))
        {
            preview = new EffectTagPreview($"Permanently raises max Tempest Energy by {teDelta}");
            return true;
        }

        // Decompose<Slot>ItemIntoAugmentResources — 9 slot variants share one shape.
        // Slot tokens observed in recipes.json: MainHand, OffHand, Hands, Chest, Leg,
        // Helm, Feet, Ring, Necklace.
        const string DecomposePrefix = "Decompose";
        const string DecomposeSuffix = "ItemIntoAugmentResources";
        if (trimmed.StartsWith(DecomposePrefix, StringComparison.Ordinal)
            && trimmed.EndsWith(DecomposeSuffix, StringComparison.Ordinal)
            && trimmed.Length > DecomposePrefix.Length + DecomposeSuffix.Length)
        {
            var slot = trimmed.Substring(DecomposePrefix.Length, trimmed.Length - DecomposePrefix.Length - DecomposeSuffix.Length);
            if (slot.Length > 0)
            {
                preview = new EffectTagPreview($"Decomposes equipped {Humanize(slot).ToLowerInvariant()} into augment resources");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a <c>Prefix(arg1,arg2,…)</c> string into the prefix and trimmed arg array.
    /// Returns <see langword="false"/> for malformed input (no parens, empty body, mismatched).
    /// </summary>
    private static bool TryParsePrefixCall(string? effect, out string prefix, out string[] args)
    {
        prefix = "";
        args = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(effect)) return false;

        var openParen = effect.IndexOf('(');
        var closeParen = effect.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen) return false;

        prefix = effect[..openParen];
        var argsBody = effect.AsSpan(openParen + 1, closeParen - openParen - 1);
        if (argsBody.IsEmpty)
        {
            args = Array.Empty<string>();
            return true;
        }

        args = argsBody.ToString().Split(',');
        return true;
    }

    private static bool TryParseResearchProgress(string? effect, out ResearchProgressPreview preview)
    {
        preview = null!;
        if (string.IsNullOrWhiteSpace(effect)) return false;
        var trimmed = effect.Trim();
        if (!trimmed.StartsWith(ResearchPrefix, StringComparison.Ordinal)) return false;
        if (trimmed.Length <= ResearchPrefix.Length) return false;

        // Strip trailing digits greedily — the topic is everything after "Research"
        // up to but not including the last digit run.
        var splitIndex = trimmed.Length;
        while (splitIndex > 0 && char.IsDigit(trimmed[splitIndex - 1])) splitIndex--;
        if (splitIndex == trimmed.Length) return false;
        if (splitIndex == ResearchPrefix.Length) return false;

        var topic = trimmed[ResearchPrefix.Length..splitIndex];
        var levelToken = trimmed[splitIndex..];
        if (!int.TryParse(levelToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)) return false;

        preview = new ResearchProgressPreview(topic, level, $"Research {Humanize(topic)}");
        return true;
    }

    private static bool TryParseXpGrant(string? effect, out XpGrantPreview preview)
    {
        preview = null!;
        if (string.IsNullOrWhiteSpace(effect)) return false;
        var trimmed = effect.Trim();
        if (!trimmed.StartsWith(GivePrefix, StringComparison.Ordinal)) return false;
        if (!trimmed.EndsWith(XpSuffix, StringComparison.Ordinal)) return false;
        if (trimmed.Length <= GivePrefix.Length + XpSuffix.Length) return false;

        var skill = trimmed[GivePrefix.Length..^XpSuffix.Length];
        if (skill.Length == 0) return false;

        preview = new XpGrantPreview(Humanize(skill));
        return true;
    }

    private static bool TryParseWordOfPower(string? effect, out WordOfPowerPreview preview)
    {
        preview = null!;
        if (string.IsNullOrWhiteSpace(effect)) return false;
        var trimmed = effect.Trim();
        if (!trimmed.StartsWith(DiscoverWordOfPowerPrefix, StringComparison.Ordinal)) return false;

        var suffix = trimmed[DiscoverWordOfPowerPrefix.Length..];
        if (suffix.Length == 0) return false;
        if (!int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return false;

        preview = new WordOfPowerPreview(n);
        return true;
    }

    private static bool TryParseLearnedAbility(string? effect, out LearnedAbilityPreview preview)
    {
        preview = null!;
        if (!TryParsePrefixCall(effect, out var prefix, out var args)) return false;
        if (!prefix.Equals(LearnAbilityPrefix, StringComparison.Ordinal)) return false;
        if (args.Length == 0) return false;

        var internalName = args[0].Trim();
        if (internalName.Length == 0) return false;

        preview = new LearnedAbilityPreview(internalName, Humanize(internalName));
        return true;
    }

    private static bool TryParseItemProducing(
        string? effect, IReferenceDataService refData, out ItemProducingPreview preview)
    {
        preview = null!;
        if (string.IsNullOrWhiteSpace(effect)) return false;
        var trimmed = effect.Trim();

        // Zero-arg cases first (no parentheses).
        if (trimmed.Equals(CreateNecroFuelTag, StringComparison.Ordinal))
        {
            preview = new ItemProducingPreview("Necromancy Fuel", IconId: null, Qualifier: null,
                ResolvedItemInternalName: null);
            return true;
        }

        // Treasure maps: Create{Region}TreasureMap{Quality} — zero-arg, no parens.
        // Try this before the parenthesised handlers since it has no '(' at all.
        if (trimmed.StartsWith(CreateTreasureMapPrefix, StringComparison.Ordinal)
            && !trimmed.Contains('(')
            && trimmed.Contains(TreasureMapInfix, StringComparison.Ordinal))
        {
            return TryBuildTreasureMap(trimmed, refData, out preview);
        }

        // Parenthesised forms.
        if (!TryParsePrefixCall(trimmed, out var prefix, out var args)) return false;

        if (prefix.Equals(BrewItemPrefix, StringComparison.Ordinal))
            return TryBuildBrewItem(args, out preview);
        if (prefix.Equals(SummonPlantPrefix, StringComparison.Ordinal))
            return TryBuildSummonPlant(args, refData, out preview);
        if (prefix.Equals(GiveNonMagicalLootProfilePrefix, StringComparison.Ordinal))
            return TryBuildLootProfile(args, out preview);
        if (prefix.StartsWith(CreateMiningSurveyPrefix, StringComparison.Ordinal))
            return TryBuildMiningSurvey(prefix, args, refData, out preview);
        if (prefix.StartsWith(CreateGeologySurveyPrefix, StringComparison.Ordinal))
            return TryBuildGeologySurvey(prefix, args, refData, out preview);

        return false;
    }

    private static bool TryBuildBrewItem(string[] args, out ItemProducingPreview preview)
    {
        preview = null!;
        // Args: (tier, skillLvl, ingredients=effects). Lift just the tier — the ingredient
        // list is a recipe-internal expression, not a renderable item handle.
        if (args.Length < 1) return false;
        if (!int.TryParse(args[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tier))
            return false;

        preview = new ItemProducingPreview(
            DisplayName: "Brewed item",
            IconId: null,
            Qualifier: $"Tier {tier}",
            ResolvedItemInternalName: null);
        return true;
    }

    private static bool TryBuildSummonPlant(string[] args, IReferenceDataService refData, out ItemProducingPreview preview)
    {
        preview = null!;
        // Args: (plantType, _, itemName~scalar~scalar~...). The third arg's leading
        // ~-token is the produced item's internal name.
        if (args.Length < 3) return false;
        var third = args[2].Trim();
        if (third.Length == 0) return false;

        var tildeIndex = third.IndexOf('~');
        var itemName = tildeIndex < 0 ? third : third[..tildeIndex];
        if (itemName.Length == 0) return false;

        if (refData.ItemsByInternalName.TryGetValue(itemName, out var item))
        {
            preview = new ItemProducingPreview(item.Name, item.IconId, Qualifier: null, item.InternalName);
        }
        else
        {
            preview = new ItemProducingPreview(Humanize(itemName), IconId: null, Qualifier: null, itemName);
        }
        return true;
    }

    private static bool TryBuildMiningSurvey(string prefix, string[] args, IReferenceDataService refData, out ItemProducingPreview preview)
    {
        preview = null!;
        if (args.Length == 0) return false;
        var itemName = args[0].Trim();
        if (itemName.Length == 0) return false;

        // Suffix after "CreateMiningSurvey" carries the tier — e.g. "1X", "5", "7Y".
        var qualifierSuffix = prefix[CreateMiningSurveyPrefix.Length..];
        var qualifier = qualifierSuffix.Length > 0
            ? $"Mining Survey {qualifierSuffix}"
            : "Mining Survey";

        if (refData.ItemsByInternalName.TryGetValue(itemName, out var item))
            preview = new ItemProducingPreview(item.Name, item.IconId, qualifier, item.InternalName);
        else
            preview = new ItemProducingPreview(Humanize(itemName), IconId: null, qualifier, itemName);
        return true;
    }

    private static bool TryBuildGeologySurvey(string prefix, string[] args, IReferenceDataService refData, out ItemProducingPreview preview)
    {
        preview = null!;
        if (args.Length == 0) return false;
        var itemName = args[0].Trim();
        if (itemName.Length == 0) return false;

        var colour = prefix[CreateGeologySurveyPrefix.Length..];
        var qualifier = colour.Length > 0
            ? $"Geology Survey · {Humanize(colour)}"
            : "Geology Survey";

        if (refData.ItemsByInternalName.TryGetValue(itemName, out var item))
            preview = new ItemProducingPreview(item.Name, item.IconId, qualifier, item.InternalName);
        else
            preview = new ItemProducingPreview(Humanize(itemName), IconId: null, qualifier, itemName);
        return true;
    }

    private static bool TryBuildTreasureMap(string trimmed, IReferenceDataService refData, out ItemProducingPreview preview)
    {
        preview = null!;
        // Shape: Create{Region}TreasureMap{Quality}, zero-arg. Region is free-form
        // (Eltibule / Ilmari / SunVale), Quality is one of Poor / Good / Great / Amazing.
        var infixIndex = trimmed.IndexOf(TreasureMapInfix, CreateTreasureMapPrefix.Length, StringComparison.Ordinal);
        if (infixIndex <= CreateTreasureMapPrefix.Length) return false;

        var region = trimmed[CreateTreasureMapPrefix.Length..infixIndex];
        var quality = trimmed[(infixIndex + TreasureMapInfix.Length)..];
        if (region.Length == 0 || quality.Length == 0) return false;

        // Item lookup: the engine names map items as TreasureMap{Region}{Quality}, so
        // reorder the components from the effect string when resolving.
        var itemInternalName = $"{TreasureMapInfix}{region}{quality}";
        var displayName = $"{Humanize(region)} Treasure Map";
        var qualifier = Humanize(quality);

        if (refData.ItemsByInternalName.TryGetValue(itemInternalName, out var item))
            preview = new ItemProducingPreview(item.Name, item.IconId, qualifier, item.InternalName);
        else
            preview = new ItemProducingPreview(displayName, IconId: null, qualifier, itemInternalName);
        return true;
    }

    private static bool TryBuildLootProfile(string[] args, out ItemProducingPreview preview)
    {
        preview = null!;
        if (args.Length == 0) return false;
        var profile = args[0].Trim();
        if (profile.Length == 0) return false;

        preview = new ItemProducingPreview(
            DisplayName: $"Loot from {Humanize(profile)}",
            IconId: null,
            Qualifier: null,
            ResolvedItemInternalName: null);
        return true;
    }

    /// <summary>Inserts spaces before capital letters: <c>UnarmedMeditationCombo1</c> → <c>"Unarmed Meditation Combo 1"</c>.</summary>
    private static string Humanize(string token)
    {
        if (string.IsNullOrEmpty(token)) return token;
        var sb = new System.Text.StringBuilder(token.Length + 8);
        for (int i = 0; i < token.Length; i++)
        {
            var ch = token[i];
            if (i > 0)
            {
                var prev = token[i - 1];
                var insertSpace = (char.IsUpper(ch) && !char.IsUpper(prev) && prev != ' ')
                               || (char.IsDigit(ch) && !char.IsDigit(prev) && prev != ' ');
                if (insertSpace) sb.Append(' ');
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
