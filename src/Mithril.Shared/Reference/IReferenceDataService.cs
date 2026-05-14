using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Misc;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;

namespace Mithril.Shared.Reference;

/// <summary>
/// Manages dev-published JSON reference data from cdn.projectgorgon.com.
/// Loaded eagerly at construction from disk cache (or bundled fallback) so
/// consumers see a populated dictionary synchronously. CDN refresh runs in
/// the background and raises <see cref="FileUpdated"/> when new data lands.
/// </summary>
public interface IReferenceDataService
{
    IReadOnlyList<string> Keys { get; }

    IReadOnlyDictionary<long, Item> Items { get; }

    /// <summary>InternalName → <see cref="Item"/> lookup. Useful when the log gives an InternalName but no item id.</summary>
    IReadOnlyDictionary<string, Item> ItemsByInternalName { get; }

    /// <summary>
    /// Catalog-side keyword → items index. Powers keyword-matched recipe ingredients
    /// (e.g. the auxiliary-crystal slot on every <c>*E</c> enchanted recipe). Rebuilt
    /// whenever <c>items.json</c> reloads.
    /// </summary>
    ItemKeywordIndex KeywordIndex { get; }

    /// <summary>recipe key (e.g. "recipe_1234") → <see cref="Recipe"/>.</summary>
    IReadOnlyDictionary<string, Recipe> Recipes { get; }

    /// <summary>InternalName → <see cref="Recipe"/> lookup. Matches RecipeCompletions keys from character exports.</summary>
    IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; }

    /// <summary>
    /// Recipes indexed by the InternalName of any item they produce (via
    /// <see cref="Recipe.ResultItems"/>, falling back to <see cref="Recipe.ProtoResultItems"/>
    /// when ResultItems is empty). Built whenever items.json or recipes.json reloads.
    /// Powers the reference-browser's item-detail "Produced by" cross-link section.
    /// Defaults to empty so test fakes don't need to opt into cross-linking.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesByProducedItem => EmptyRecipeIndex;

    /// <summary>
    /// Recipes indexed by the InternalName of any item they consume as an ingredient via a
    /// direct <see cref="RecipeItemIngredient"/>. <see cref="RecipeKeywordIngredient"/> slots
    /// are kind-based (e.g. "any Crystal") and don't map to a single InternalName — they're
    /// surfaced through <see cref="KeywordsUsedInRecipeSlots"/> instead. Built whenever
    /// items.json or recipes.json reloads. Defaults to empty so test fakes don't need to opt
    /// into cross-linking.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesByIngredientItem => EmptyRecipeIndex;

    /// <summary>
    /// The flat set of every distinct keyword tag that appears in any
    /// <see cref="RecipeKeywordIngredient.ItemKeys"/> across all recipes. Powers the item-detail
    /// "Used as" section: an item's chip set is <c>item.Keywords ∩ KeywordsUsedInRecipeSlots</c>,
    /// so chips only appear for keywords that actually lead to at least one recipe. Built
    /// whenever recipes.json reloads. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyCollection<string> KeywordsUsedInRecipeSlots => Array.Empty<string>();

    /// <summary>
    /// Friendly display names for keyword tags, sourced from singleton-slot Descs in recipes
    /// (looked up via <c>strings_all["recipe_&lt;id&gt;_Ingredients_&lt;idx&gt;_Desc"]</c>, falling
    /// back to <see cref="RecipeKeywordIngredient.Desc"/> from recipes.json). Only singleton slots
    /// — those whose <see cref="RecipeKeywordIngredient.ItemKeys"/> has exactly one entry — are
    /// considered, because composite-tuple Descs describe the AND-matched composite, not any one
    /// keyword. Keywords whose only friendly Desc is identical to the raw tag are omitted (callers
    /// should treat "missing" as "use the raw tag, optionally split by camel-case"). First match
    /// wins (recipe-id iteration order is stable). Built whenever recipes.json or strings_all.json
    /// reloads. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, string> KeywordDisplayNames => EmptyStringMap;

    private static readonly IReadOnlyDictionary<string, string> EmptyStringMap
        = new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Recipe>> EmptyRecipeIndex
        = new Dictionary<string, IReadOnlyList<Recipe>>(StringComparer.Ordinal);

    /// <summary>Skill key (e.g. "Meditation", "AncillaryArmorAugmentBrewing") → SkillEntry.
    /// Keys are id-shaped (ASCII identifier-safe) and match recipes' RewardSkill field.
    /// For the human-readable in-game name use <see cref="SkillEntry.DisplayName"/>.</summary>
    IReadOnlyDictionary<string, SkillEntry> Skills { get; }

    /// <summary>XP table InternalName (e.g. "TypicalNoncombatSkill") → XpTableEntry.</summary>
    IReadOnlyDictionary<string, XpTableEntry> XpTables { get; }

    /// <summary>NPC key (e.g. "NPC_Marna") → NpcEntry with gift preferences.</summary>
    IReadOnlyDictionary<string, NpcEntry> Npcs { get; }

    /// <summary>
    /// NPC <c>InternalName</c> (the JSON envelope key, e.g. <c>"NPC_Marna"</c>, <c>"Altar_Druid"</c>) →
    /// the full <see cref="Npc"/> POCO from <c>npcs.json</c>. Unlike <see cref="Npcs"/>, which is a
    /// slim projection consumed by Arwen for gift calculation, this dictionary exposes Services,
    /// Preferences, ItemGifts, Pos, AreaFriendlyName etc. for Silmarillion's master-detail tab.
    /// Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, Npc> NpcsByInternalName => EmptyNpcMap;

    /// <summary>
    /// Reverse index from NPC <c>InternalName</c> → recipes the NPC teaches, derived from
    /// <see cref="RecipeSources"/> entries with <c>Type == "Training"</c>. Built whenever
    /// recipes.json, npcs.json, or sources_recipes.json reloads. Defaults to empty so test
    /// fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesTaughtByNpc => EmptyRecipeIndex;

    /// <summary>
    /// Reverse index from NPC <c>InternalName</c> → items the NPC sells, derived from
    /// <see cref="ItemSources"/> entries with <c>Type == "Vendor"</c>. Built whenever items.json,
    /// npcs.json, or sources_items.json reloads. Defaults to empty so test fakes don't need to
    /// opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Item>> ItemsSoldByNpc => EmptyItemIndex;

    private static readonly IReadOnlyDictionary<string, Npc> EmptyNpcMap
        = new Dictionary<string, Npc>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Item>> EmptyItemIndex
        = new Dictionary<string, IReadOnlyList<Item>>(StringComparer.Ordinal);

    /// <summary>
    /// Area key (e.g. <c>"AreaSerbule"</c>) → friendly display names. Pulled from
    /// <c>areas.json</c>; primary use is resolving area codes that appear on
    /// non-NPC item sources (drops, quests).
    /// </summary>
    IReadOnlyDictionary<string, AreaEntry> Areas { get; }

    /// <summary>
    /// Item InternalName → sources describing how the item can be obtained
    /// (Vendor / Recipe / Quest / Monster / HangOut / NpcGift / Barter / Angling / …).
    /// Pulled from <c>sources_items.json</c>.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; }

    /// <summary>
    /// Recipe InternalName → sources describing how the recipe is acquired
    /// (Training / Skill / Effect / Quest / NpcGift / …). Pulled from
    /// <c>sources_recipes.json</c>. Mirrors <see cref="ItemSources"/>. Defaults
    /// to an empty dictionary so existing test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<RecipeSource>> RecipeSources => EmptyRecipeSourceIndex;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RecipeSource>> EmptyRecipeSourceIndex
        = new Dictionary<string, IReadOnlyList<RecipeSource>>(StringComparer.Ordinal);

    /// <summary>
    /// Ability envelope key (e.g. <c>"ability_42"</c>) → the full <see cref="Ability"/> POCO from
    /// <c>abilities.json</c>. Carries the wide ability metadata surface (animations,
    /// prerequisites, ammo, sidebar visibility, the nested <see cref="Ability.PvE"/> stat block,
    /// etc.) consumed by Silmarillion's Abilities tab. Defaults to empty so test fakes don't
    /// need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, Ability> Abilities => EmptyAbilityMap;

    /// <summary>
    /// Ability <c>InternalName</c> (e.g. <c>"Sword1"</c>, <c>"Mentalism5"</c>) →
    /// <see cref="Ability"/>. Used by the navigator + chip cross-links and by
    /// <see cref="IEntityNameResolver"/> to project ability InternalNames to display names.
    /// Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, Ability> AbilitiesByInternalName => EmptyAbilityMap;

    /// <summary>
    /// Skill key (e.g. <c>"Sword"</c>, <c>"Mentalism"</c>) → abilities tagged to that skill via
    /// <see cref="Ability.Skill"/>. Powers the Abilities-tab master-list skill facet. Built
    /// whenever <c>abilities.json</c> reloads. Defaults to empty so test fakes don't need to
    /// opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesBySkill => EmptyAbilityIndex;

    /// <summary>
    /// Reverse index from prior ability <c>InternalName</c> → abilities that name it in
    /// <see cref="Ability.UpgradeOf"/>. Powers the ability-detail "Upgrades to" reverse view
    /// on the prior ability. Built whenever <c>abilities.json</c> reloads. Defaults to empty
    /// so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesUpgradingFrom => EmptyAbilityIndex;

    /// <summary>
    /// Reverse index from <see cref="Ability.AbilityGroup"/> → all abilities in that group.
    /// Powers the ability-detail "Other abilities in group" roster section. Built whenever
    /// <c>abilities.json</c> reloads. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesInGroup => EmptyAbilityIndex;

    /// <summary>
    /// Reverse index from NPC <c>InternalName</c> → abilities the NPC teaches, derived from
    /// <see cref="AbilitySources"/> entries with <c>Type == "Training"</c>. Built whenever
    /// <c>abilities.json</c>, <c>npcs.json</c>, or <c>sources_abilities.json</c> reloads.
    /// Mirrors <see cref="RecipesTaughtByNpc"/>. Defaults to empty so test fakes don't need
    /// to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesTaughtByNpc => EmptyAbilityIndex;

    /// <summary>
    /// Ability <c>InternalName</c> → sources describing how the ability is acquired
    /// (Training / Skill / Quest / Effect / …). Pulled from <c>sources_abilities.json</c>.
    /// Mirrors <see cref="ItemSources"/> and <see cref="RecipeSources"/>. Defaults to empty
    /// so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<AbilitySource>> AbilitySources => EmptyAbilitySourceIndex;

    private static readonly IReadOnlyDictionary<string, Ability> EmptyAbilityMap
        = new Dictionary<string, Ability>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Ability>> EmptyAbilityIndex
        = new Dictionary<string, IReadOnlyList<Ability>>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<AbilitySource>> EmptyAbilitySourceIndex
        = new Dictionary<string, IReadOnlyList<AbilitySource>>(StringComparer.Ordinal);

    /// <summary>
    /// Placeholder token (e.g. <c>"MAX_ARMOR"</c>) → <see cref="AttributeEntry"/> with the
    /// human-readable label and formatting hints used to render <see cref="Item.EffectDescs"/>.
    /// Pulled from <c>attributes.json</c>.
    /// </summary>
    IReadOnlyDictionary<string, AttributeEntry> Attributes { get; }

    /// <summary>
    /// Power InternalName (e.g. <c>"ShamanicHeadArmor"</c>, <c>"ArcheryBoost"</c>) →
    /// <see cref="PowerEntry"/> describing the per-tier <see cref="EffectDescs"/> used by
    /// <c>AddItemTSysPower</c> augmentation recipes. Pulled from <c>tsysclientinfo.json</c>.
    /// </summary>
    IReadOnlyDictionary<string, PowerEntry> Powers { get; }

    /// <summary>
    /// Random-roll profile name (e.g. <c>"All"</c>, <c>"Sword"</c>, <c>"MainHandAugment"</c>) →
    /// list of power InternalNames eligible to roll on items carrying that <see cref="Item.TSysProfile"/>.
    /// Pulled from <c>tsysprofiles.json</c>; consumed by <c>ExtractTSysPower</c> and
    /// <c>TSysCraftedEquipment</c> "Possible augments" previews.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; }

    /// <summary>
    /// Quest envelope key (e.g. <c>"quest_10001"</c>) → the full <see cref="Quest"/> POCO from
    /// <c>quests.json</c>. Carries typed <see cref="Quest.Requirements"/>, <see cref="Quest.Rewards"/>,
    /// <see cref="Quest.Objectives"/>, follow-ups, NPC refs, reuse timers, and the description /
    /// success / preface text blocks needed by Silmarillion's Quests tab and Gandalf's repeatable-
    /// quest timer source.
    /// </summary>
    IReadOnlyDictionary<string, Quest> Quests { get; }

    /// <summary>
    /// Quest <c>InternalName</c> → <see cref="Quest"/> POCO. Matches Quest sources in
    /// <c>sources_items.json</c> and the per-character <c>quests.json</c> active-journal entries.
    /// </summary>
    IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; }

    /// <summary>
    /// Reverse index from NPC <c>InternalName</c> → quests where the NPC is either the giver
    /// (<see cref="Quest.QuestNpc"/>) or the favor anchor (<see cref="Quest.FavorNpc"/>). Built
    /// whenever <c>quests.json</c> or <c>npcs.json</c> reloads. Powers the NPCs-tab "Quests
    /// given" section. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Quest>> QuestsByGiverNpc => EmptyQuestIndex;

    /// <summary>
    /// Reverse index from item <c>InternalName</c> → quests whose <see cref="Quest.Rewards_Items"/>
    /// awards that item. Built whenever <c>quests.json</c> or <c>items.json</c> reloads. Powers the
    /// Items-tab "Awarded by" section. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Quest>> QuestsRewardingItem => EmptyQuestIndex;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Quest>> EmptyQuestIndex
        = new Dictionary<string, IReadOnlyList<Quest>>(StringComparer.Ordinal);

    /// <summary>
    /// Flat ordered list of every <see cref="DirectedGoal"/> from
    /// <c>directedgoals.json</c> — the "stuff to do" in-game guidance panel. Mixed-type
    /// list of category headers (<see cref="DirectedGoal.IsCategoryGate"/> = true,
    /// e.g. <c>"Anagoge Island"</c>, <c>"Serbule Hills"</c>) and per-area sub-goals
    /// (e.g. <c>"Grow a potato"</c>, <c>"Meet Nightshade"</c>) keyed to a parent gate
    /// via <see cref="DirectedGoal.CategoryGateId"/>. Defaults to empty so test fakes
    /// don't need to opt in.
    /// </summary>
    IReadOnlyList<DirectedGoal> DirectedGoals => Array.Empty<DirectedGoal>();

    /// <summary>
    /// Flat list of conditional "if an ability has these keywords, the listed
    /// attributes apply" rules from <c>abilitykeywords.json</c>. The predicate is
    /// <see cref="AbilityKeyword.MustHaveAbilityKeywords"/>; the consequents are the
    /// <c>AttributesThat*</c> lists. Defaults to empty so test fakes don't need to
    /// opt in. Consumed by the Effects tab (#244) — not folded into per-ability
    /// detail, since the rules are keyed by keyword predicate, not by ability InternalName.
    /// </summary>
    IReadOnlyList<AbilityKeyword> AbilityKeywordRules => Array.Empty<AbilityKeyword>();

    /// <summary>
    /// Flat list of conditional damage-over-time rules from <c>abilitydynamicdots.json</c>
    /// that layer on top of an ability at runtime when <see cref="AbilityDynamicDot.ReqAbilityKeywords"/>,
    /// <see cref="AbilityDynamicDot.ReqActiveSkill"/>, and <see cref="AbilityDynamicDot.ReqEffectKeywords"/>
    /// predicates match. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyList<AbilityDynamicDot> AbilityDynamicDots => Array.Empty<AbilityDynamicDot>();

    /// <summary>
    /// Flat list of conditional tooltip-value rules from
    /// <c>abilitydynamicspecialvalues.json</c> that layer a labelled value onto an
    /// ability's tooltip when <see cref="AbilityDynamicSpecialValue.ReqAbilityKeywords"/>
    /// and <see cref="AbilityDynamicSpecialValue.ReqEffectKeywords"/> predicates match.
    /// Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyList<AbilityDynamicSpecialValue> AbilityDynamicSpecialValues => Array.Empty<AbilityDynamicSpecialValue>();

    /// <summary>
    /// All localizable strings from <c>strings_all.json</c>, keyed by their
    /// dotted/slashed string ID. Primary use today is friendly-name resolution
    /// for chest / NPC / cow / tree prefabs via the
    /// <c>npc_&lt;Area&gt;/&lt;InternalName&gt;_Name</c> convention (with
    /// <c>npc_&lt;InternalName&gt;_Name</c> fallback for area-agnostic
    /// prefabs). See [Player-Log-Signals#display-name-resolution-strings_all]
    /// in the wiki.
    /// </summary>
    IReadOnlyDictionary<string, string> Strings { get; }

    ReferenceFileSnapshot GetSnapshot(string key);

    Task RefreshAsync(string key, CancellationToken ct = default);

    Task RefreshAllAsync(CancellationToken ct = default);

    /// <summary>Fire-and-forget refresh of every known file. Intended for app start.</summary>
    void BeginBackgroundRefresh();

    event EventHandler<string>? FileUpdated;
}
