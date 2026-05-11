using Mithril.Reference.Models.Items;
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

    /// <summary>Skill key (e.g. "Meditation", "AncillaryArmorAugmentBrewing") → SkillEntry.
    /// Keys are id-shaped (ASCII identifier-safe) and match recipes' RewardSkill field.
    /// For the human-readable in-game name use <see cref="SkillEntry.DisplayName"/>.</summary>
    IReadOnlyDictionary<string, SkillEntry> Skills { get; }

    /// <summary>XP table InternalName (e.g. "TypicalNoncombatSkill") → XpTableEntry.</summary>
    IReadOnlyDictionary<string, XpTableEntry> XpTables { get; }

    /// <summary>NPC key (e.g. "NPC_Marna") → NpcEntry with gift preferences.</summary>
    IReadOnlyDictionary<string, NpcEntry> Npcs { get; }

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

    /// <summary>Quest key (e.g. <c>"quest_10001"</c>) → <see cref="QuestEntry"/>.</summary>
    IReadOnlyDictionary<string, QuestEntry> Quests { get; }

    /// <summary>InternalName → <see cref="QuestEntry"/> lookup. Matches Quest sources in <c>sources_items.json</c>.</summary>
    IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName { get; }

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
