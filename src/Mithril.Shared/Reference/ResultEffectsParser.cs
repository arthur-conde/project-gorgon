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

    // Recipe-system prefixes.
    private const string AdjustRecipeReuseTimePrefix = "AdjustRecipeReuseTime";

    // Equipment-property prefixes.
    private const string BoostEquipAdvancementPrefix = "BoostItemEquipAdvancementTable";
    private const string CraftingEnhanceItemPrefix = "CraftingEnhanceItem";
    private const string CraftingEnhanceModSuffix = "Mod";
    private const string RepairItemDurabilityPrefix = "RepairItemDurability";
    private const string CraftingResetItemTag = "CraftingResetItem";
    private const string TransmogItemAppearanceTag = "TransmogItemAppearance";

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

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="EquipBonusPreview"/>
    /// per well-formed <c>BoostItemEquipAdvancementTable(table)</c> entry. The
    /// <c>table</c> token (e.g. <c>ForetoldHammerDamage</c>) is humanised for display;
    /// the raw token stays on the preview for future deep-linking.
    /// </summary>
    public static IReadOnlyList<EquipBonusPreview> ParseEquipBonuses(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<EquipBonusPreview>();
        foreach (var effect in effects)
        {
            if (TryParseEquipBonus(effect, out var preview))
                previews.Add(preview);
        }
        return previews;
    }

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="CraftingEnhancePreview"/>
    /// per recognised crafting-enhancement entry. Six schema variants flow through:
    /// <list type="bullet">
    ///   <item><c>CraftingEnhanceItem{Element}Mod(scalar,M)</c> — element damage mod
    ///   (Fire/Cold/Electricity/Psychic/Nature/Darkness); scalar &lt; 1 renders as a
    ///   percentage (<c>+2%</c>) and M is the stack cap.</item>
    ///   <item><c>CraftingEnhanceItemArmor(N,M)</c> and
    ///   <c>CraftingEnhanceItemPockets(N,M)</c> — flat <c>+N</c> bonus, M stack cap.</item>
    ///   <item><c>RepairItemDurability(min,max,itemLevel,minDmg,maxDmg)</c> — surfaces
    ///   the level cap so the player can see what tier of gear the recipe targets.</item>
    ///   <item><c>CraftingResetItem</c> and <c>TransmogItemAppearance</c> — zero-arg
    ///   tags that emit a fixed line.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<CraftingEnhancePreview> ParseCraftingEnhancements(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<CraftingEnhancePreview>();
        foreach (var effect in effects)
        {
            if (TryParseCraftingEnhancement(effect, out var preview))
                previews.Add(preview);
        }
        return previews;
    }

    /// <summary>
    /// Parse <paramref name="effects"/> and return one <see cref="RecipeCooldownPreview"/>
    /// per well-formed <c>AdjustRecipeReuseTime(deltaSeconds, condition)</c> entry. The
    /// delta is rendered as a humanised duration (s/m/h/d) and the condition (e.g.
    /// <c>QuarterMoon</c>) is humanised when present.
    /// </summary>
    public static IReadOnlyList<RecipeCooldownPreview> ParseRecipeCooldowns(
        IReadOnlyList<string>? effects, IReferenceDataService refData)
    {
        if (effects is null || effects.Count == 0) return [];

        var previews = new List<RecipeCooldownPreview>();
        foreach (var effect in effects)
        {
            if (TryParseRecipeCooldown(effect, out var preview))
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
        ("TeleportToBoundMushroomCircle", "Teleports to bound mushroom circle {0}"),
        ("TeleportToBoundTeleportCircle", "Teleports to bound teleport circle {0}"),
        ("BindToMushroomCircle", "Binds to mushroom circle {0}"),
        ("BindToTeleportCircle", "Binds to teleport circle {0}"),
        ("SpawnPlayerPortal", "Spawns player portal {0}"),
        ("StoragePortal", "Storage portal {0}"),
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

        // Mushroom / teleport / portal — zero-arg fixed forms.
        ["SaveCurrentMushroomCircle"] = "Saves current mushroom circle",
        ["TeleportToLastUsedMushroomCircle"] = "Teleports to last-used mushroom circle",
        ["TeleportToNearbyMushroomCircle"] = "Teleports to nearby mushroom circle",
        ["SaveCurrentTeleportCircle"] = "Saves current teleport circle",
        ["TeleportToLastUsedTeleportSpot"] = "Teleports to last-used teleport spot",
        ["TeleportToSpontaneousSpot"] = "Teleports to a spontaneous spot",
        ["TeleportToMostCommonSpot"] = "Teleports to most-common spot",
        ["TeleportToGuildHall"] = "Teleports to guild hall",
        ["TeleportToEntrypoint"] = "Teleports to entry point",

        // Cosmetic / fairy / drink behavioural tags.
        ["CraftingDyeItem"] = "Dyes the item",
        ["HairCleaner"] = "Cleans hair colour",
        ["DispelFairyLight"] = "Dispels fairy light",
        ["SummonFairyLight"] = "Summons fairy light",
        ["DrinkNectar"] = "Drinks nectar",
        ["DeployBeerBarrel"] = "Deploys beer barrel",

        // Survey / divination / metadata.
        ["MoonPhaseCheck"] = "Checks moon phase",
        ["WeatherReport"] = "Reports current weather",
        ["ShowWardenEvents"] = "Shows warden events",

        // Fishing / utility.
        ["CheckForBonusFishScales"] = "Checks for bonus fish scales",
        ["CheckForBonusPerfectCotton"] = "Checks for bonus perfect cotton",
        ["SendItemToSaddlebag"] = "Sends item to saddlebag",
        ["ApplyRacingRibbonToReins"] = "Applies racing ribbon to reins",
        ["SummonStatehelm"] = "Summons statehelm",
        ["SummonPovusPaleomonster"] = "Summons Povus paleomonster",
        ["HoplologyStudy"] = "Hoplology study",

        // Cosmetic permanent-form polymorphs.
        ["PolymorphRabbitPermanentBlue"] = "Polymorphs to a permanent blue rabbit",
        ["PolymorphRabbitPermanentPurple"] = "Polymorphs to a permanent purple rabbit",
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

        // Non-augment decompose variants — Decompose{Source}Into{Substance}.
        // Distinct from the augment-slot handler above because the suffix names a
        // substance (Phlogiston / CrystalIce / FairyDust / Essence) instead of
        // "AugmentResources". Shape examples:
        //   DecomposeItemIntoPhlogiston, DecomposeItemIntoCrystalIce,
        //   DecomposeItemIntoFairyDust, DecomposeDemonOreIntoEssence,
        //   DecomposeFoodIntoCrystalIce_Wine.
        if (trimmed.StartsWith(DecomposePrefix, StringComparison.Ordinal))
        {
            var rest = trimmed[DecomposePrefix.Length..];
            var intoIndex = rest.IndexOf("Into", StringComparison.Ordinal);
            if (intoIndex > 0)
            {
                var source = rest[..intoIndex];
                var substance = rest[(intoIndex + "Into".Length)..];
                if (source.Length > 0 && substance.Length > 0
                    && !substance.Equals("AugmentResources", StringComparison.Ordinal))
                {
                    preview = new EffectTagPreview(
                        $"Decomposes {Humanize(source).ToLowerInvariant()} into {Humanize(substance).ToLowerInvariant()}");
                    return true;
                }
            }
        }

        // HelpMsg_{topic} — strip prefix + humanise.
        if (trimmed.StartsWith("HelpMsg_", StringComparison.Ordinal)
            && trimmed.Length > "HelpMsg_".Length)
        {
            var topic = trimmed["HelpMsg_".Length..];
            preview = new EffectTagPreview($"Help: {Humanize(topic)}");
            return true;
        }

        // StorageCrateDruid{N}Items — special suffix shape.
        if (trimmed.StartsWith("StorageCrateDruid", StringComparison.Ordinal)
            && trimmed.EndsWith("Items", StringComparison.Ordinal))
        {
            var middle = trimmed["StorageCrateDruid".Length..^"Items".Length];
            if (middle.Length > 0
                && int.TryParse(middle, NumberStyles.Integer, CultureInfo.InvariantCulture, out var crateSize))
            {
                preview = new EffectTagPreview($"Druid storage crate ({crateSize} items)");
                return true;
            }
        }

        // Parametrised: Teleport(area, name).
        if (trimmed.StartsWith("Teleport(", StringComparison.Ordinal)
            && TryParsePrefixCall(trimmed, out var tpPrefix, out var tpArgs)
            && tpPrefix.Equals("Teleport", StringComparison.Ordinal)
            && tpArgs.Length >= 2)
        {
            var area = tpArgs[0].Trim();
            var spot = tpArgs[1].Trim();
            if (area.Length > 0 && spot.Length > 0)
            {
                // Strip "Area" prefix from area for cleaner display ("AreaSerbule" → "Serbule").
                if (area.StartsWith("Area", StringComparison.Ordinal) && area.Length > 4)
                    area = area[4..];
                preview = new EffectTagPreview($"Teleports to {Humanize(spot)} in {Humanize(area)}");
                return true;
            }
        }

        // Parametrised: DeltaCurFairyEnergy(N) — signed integer delta.
        if (trimmed.StartsWith("DeltaCurFairyEnergy", StringComparison.Ordinal)
            && TryParsePrefixCall(trimmed, out var feePrefix, out var feeArgs)
            && feePrefix.Equals("DeltaCurFairyEnergy", StringComparison.Ordinal)
            && feeArgs.Length >= 1
            && int.TryParse(feeArgs[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var feeDelta))
        {
            var verb = feeDelta < 0 ? "Reduces" : "Adds";
            preview = new EffectTagPreview($"{verb} fairy energy by {Math.Abs(feeDelta)}");
            return true;
        }

        // Parametrised: ConsumeItemUses(template, N).
        if (trimmed.StartsWith("ConsumeItemUses", StringComparison.Ordinal)
            && TryParsePrefixCall(trimmed, out var ciuPrefix, out var ciuArgs)
            && ciuPrefix.Equals("ConsumeItemUses", StringComparison.Ordinal)
            && ciuArgs.Length >= 2
            && int.TryParse(ciuArgs[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uses))
        {
            var template = ciuArgs[0].Trim();
            preview = new EffectTagPreview($"Consumes {uses} use(s) of {Humanize(template)}");
            return true;
        }

        // Particle_{kind} — silent allow-list. The game uses these as display markers
        // for effect particles; they never carry player-meaningful semantics, so we
        // recognise the shape and intentionally emit no preview rather than letting
        // the generic fallback below humanise them.
        if (trimmed.StartsWith("Particle_", StringComparison.Ordinal))
            return false;

        // Generic fallback — emit a humanised preview for any unrecognised prefix
        // that looks like a clean identifier (optionally with parens). Belt-and-
        // suspenders coverage so a new game patch's unfamiliar prefix renders
        // *something* instead of dropping silently. Named handlers above remain
        // the source of truth for player-meaningful text.
        if (TryHumanizeAsFallback(trimmed, out var fallback))
        {
            preview = new EffectTagPreview(fallback);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Prefixes that have dedicated typed parsers (<see cref="ParseCraftedGear"/>,
    /// <see cref="ParseAugments"/>, etc.). The generic fallback in
    /// <see cref="TryParseEffectTag"/> intentionally skips these so calling
    /// <see cref="ParseEffectTags"/> on the same input as a typed parser doesn't
    /// produce a duplicate humanised line on top of the structured chip.
    /// </summary>
    private static readonly HashSet<string> ExcludedFallbackPrefixes = new(StringComparer.Ordinal)
    {
        // Crafted gear / augment / wax / pool families.
        "TSysCraftedEquipment", "GiveTSysItem", "CraftSimpleTSysItem",
        "AddItemTSysPower", "AddItemTSysPowerWax",
        "BestowRecipeIfNotKnown", "CraftWaxItem", "ExtractTSysPower",
        // Item-producing.
        "BrewItem", "SummonPlant", "CreateNecroFuel", "GiveNonMagicalLootProfile",
        // Equipment-property + recipe-system.
        "BoostItemEquipAdvancementTable", "RepairItemDurability",
        "CraftingResetItem", "TransmogItemAppearance",
        "AdjustRecipeReuseTime",
        // Knowledge / progression.
        "DiscoverWordOfPower", "LearnAbility",
    };

    /// <summary>
    /// Last-resort humanizer for unrecognised effect strings. Accepts inputs of
    /// the shape <c>Identifier</c> or <c>Identifier(args)</c> where Identifier
    /// is PascalCase / snake_case / digit-suffixed — i.e. anything that looks
    /// like a game-engine effect token rather than free-text. Rejects strings
    /// starting with whitespace, lowercase, or punctuation so we don't surface
    /// noise that slipped through some other pathway. Also rejects prefixes
    /// claimed by a typed parser (see <see cref="ExcludedFallbackPrefixes"/>) and
    /// the prefix-match families (<c>Research</c>, <c>Give*Xp</c>,
    /// <c>CreateMiningSurvey*</c>, <c>CreateGeologySurvey*</c>,
    /// <c>Create*TreasureMap*</c>, <c>CraftingEnhanceItem*</c>).
    /// </summary>
    private static bool TryHumanizeAsFallback(string trimmed, out string display)
    {
        display = "";
        if (trimmed.Length == 0) return false;
        if (!char.IsUpper(trimmed[0])) return false;

        // Lift the prefix-call structure if present, but don't require it.
        var prefix = trimmed;
        var openParen = trimmed.IndexOf('(');
        if (openParen > 0)
        {
            if (!TryParsePrefixCall(trimmed, out var p, out _)) return false;
            prefix = p;
        }

        // Identifier-shape check: PascalCase / underscores / digits only.
        foreach (var ch in prefix)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_') return false;
        }

        // Skip prefixes already claimed by a typed parser.
        if (ExcludedFallbackPrefixes.Contains(prefix)) return false;
        if (prefix.StartsWith(ResearchPrefix, StringComparison.Ordinal)
            && prefix.Length > ResearchPrefix.Length
            && char.IsUpper(prefix[ResearchPrefix.Length])) return false;
        if (prefix.StartsWith(GivePrefix, StringComparison.Ordinal)
            && prefix.EndsWith(XpSuffix, StringComparison.Ordinal)
            && prefix.Length > GivePrefix.Length + XpSuffix.Length) return false;
        if (prefix.StartsWith(CreateMiningSurveyPrefix, StringComparison.Ordinal)) return false;
        if (prefix.StartsWith(CreateGeologySurveyPrefix, StringComparison.Ordinal)) return false;
        if (prefix.StartsWith(CraftingEnhanceItemPrefix, StringComparison.Ordinal)) return false;
        // Treasure maps (zero-arg, Create{Region}TreasureMap{Quality}).
        if (prefix.StartsWith(CreateTreasureMapPrefix, StringComparison.Ordinal)
            && prefix.Contains(TreasureMapInfix, StringComparison.Ordinal)) return false;

        display = Humanize(prefix);
        return true;
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

    private static bool TryParseEquipBonus(string? effect, out EquipBonusPreview preview)
    {
        preview = null!;
        if (!TryParsePrefixCall(effect, out var prefix, out var args)) return false;
        if (!prefix.Equals(BoostEquipAdvancementPrefix, StringComparison.Ordinal)) return false;
        if (args.Length == 0) return false;

        var table = args[0].Trim();
        if (table.Length == 0) return false;

        preview = new EquipBonusPreview(table, Humanize(table));
        return true;
    }

    private static bool TryParseCraftingEnhancement(string? effect, out CraftingEnhancePreview preview)
    {
        preview = null!;
        if (string.IsNullOrWhiteSpace(effect)) return false;
        var trimmed = effect.Trim();

        // Zero-arg cases first (no parentheses).
        if (trimmed.Equals(CraftingResetItemTag, StringComparison.Ordinal))
        {
            preview = new CraftingEnhancePreview("Resets item to stock shape", null);
            return true;
        }
        if (trimmed.Equals(TransmogItemAppearanceTag, StringComparison.Ordinal))
        {
            preview = new CraftingEnhancePreview("Applies glamour to item", null);
            return true;
        }

        // Parenthesised forms.
        if (!TryParsePrefixCall(trimmed, out var prefix, out var args)) return false;

        if (prefix.Equals(RepairItemDurabilityPrefix, StringComparison.Ordinal))
            return TryBuildRepairDurability(args, out preview);

        if (prefix.StartsWith(CraftingEnhanceItemPrefix, StringComparison.Ordinal))
            return TryBuildCraftingEnhanceItem(prefix, args, out preview);

        return false;
    }

    private static bool TryBuildCraftingEnhanceItem(string prefix, string[] args, out CraftingEnhancePreview preview)
    {
        preview = null!;
        // Suffix after "CraftingEnhanceItem" is either an element + "Mod"
        // (FireMod / ColdMod / etc.) or a flat property (Armor / Pockets).
        var suffix = prefix[CraftingEnhanceItemPrefix.Length..];
        if (suffix.Length == 0) return false;

        var isElementMod = suffix.EndsWith(CraftingEnhanceModSuffix, StringComparison.Ordinal);
        if (isElementMod)
        {
            if (args.Length < 2) return false;
            var element = suffix[..^CraftingEnhanceModSuffix.Length];
            if (element.Length == 0) return false;
            if (!decimal.TryParse(args[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var scalar)) return false;
            if (!int.TryParse(args[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var magnitude)) return false;

            var scalarText = scalar < 1m
                ? $"+{(scalar * 100m):0.##}%"
                : $"+{scalar.ToString("0.##", CultureInfo.InvariantCulture)}";
            preview = new CraftingEnhancePreview(
                $"{Humanize(element)} damage",
                $"{scalarText} (max {magnitude})");
            return true;
        }

        // Flat-bonus shape — e.g. CraftingEnhanceItemArmor(3,5), CraftingEnhanceItemPockets(2,12).
        if (args.Length < 2) return false;
        if (!int.TryParse(args[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return false;
        if (!int.TryParse(args[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stackCap)) return false;

        preview = new CraftingEnhancePreview(
            Humanize(suffix),
            $"+{n} (stack to {stackCap})");
        return true;
    }

    private static bool TryBuildRepairDurability(string[] args, out CraftingEnhancePreview preview)
    {
        preview = null!;
        // Args: (minDur, maxDur, itemLevel, minDmgRange, maxDmgRange). The level
        // is the most player-meaningful field — surface it; -1 sentinel means
        // "any level" (Mastercrafted-style recipes).
        if (args.Length < 3) return false;
        if (!int.TryParse(args[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
            return false;

        var detail = level < 0 ? "any level" : $"items at level {level}";
        preview = new CraftingEnhancePreview("Repairs item durability", detail);
        return true;
    }

    private static bool TryParseRecipeCooldown(string? effect, out RecipeCooldownPreview preview)
    {
        preview = null!;
        if (!TryParsePrefixCall(effect, out var prefix, out var args)) return false;
        if (!prefix.Equals(AdjustRecipeReuseTimePrefix, StringComparison.Ordinal)) return false;
        if (args.Length == 0) return false;
        if (!int.TryParse(args[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
            return false;

        string? condition = null;
        if (args.Length >= 2)
        {
            var rawCondition = args[1].Trim();
            if (rawCondition.Length > 0) condition = Humanize(rawCondition);
        }

        var verb = delta < 0 ? "Reduces" : "Adds";
        var magnitude = HumanizeDuration(Math.Abs(delta));
        var displayText = condition is null
            ? $"{verb} cooldown by {magnitude}"
            : $"{verb} cooldown by {magnitude} on {condition}";

        preview = new RecipeCooldownPreview(delta, condition, displayText);
        return true;
    }

    /// <summary>Renders an absolute second count as <c>"1d"</c> / <c>"2h 30m"</c> / <c>"45s"</c>-style text.</summary>
    private static string HumanizeDuration(int seconds)
    {
        if (seconds <= 0) return "0s";

        const int Day = 86400;
        const int Hour = 3600;
        const int Minute = 60;

        var days = seconds / Day; seconds %= Day;
        var hours = seconds / Hour; seconds %= Hour;
        var minutes = seconds / Minute; seconds %= Minute;

        var parts = new List<string>(4);
        if (days > 0) parts.Add($"{days}d");
        if (hours > 0) parts.Add($"{hours}h");
        if (minutes > 0) parts.Add($"{minutes}m");
        if (seconds > 0) parts.Add($"{seconds}s");
        return string.Join(' ', parts);
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
