using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Mithril.Reference;
using Mithril.Reference.Serialization;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Diagnostics.Performance;
using Ability = Mithril.Reference.Models.Abilities.Ability;
using Effect = Mithril.Reference.Models.Effects.Effect;
using Item = Mithril.Reference.Models.Items.Item;
using PocoArea = Mithril.Reference.Models.Misc.Area;
using PocoAttribute = Mithril.Reference.Models.Misc.AttributeDef;
using PocoLandmark = Mithril.Reference.Models.Misc.Landmark;
using Lorebook = Mithril.Reference.Models.Misc.Lorebook;
using LorebookInfo = Mithril.Reference.Models.Misc.LorebookInfo;
using LorebookCategoryInfo = Mithril.Reference.Models.Misc.LorebookCategoryInfo;
using PocoNpc = Mithril.Reference.Models.Npcs.Npc;
using PocoNpcPreference = Mithril.Reference.Models.Npcs.NpcPreference;
using PocoNpcService = Mithril.Reference.Models.Npcs.NpcService;
using PocoNpcStoreService = Mithril.Reference.Models.Npcs.StoreService;
using PocoPower = Mithril.Reference.Models.Misc.PowerProfile;
using DirectedGoal = Mithril.Reference.Models.Misc.DirectedGoal;
using AbilityKeyword = Mithril.Reference.Models.Misc.AbilityKeyword;
using AbilityDynamicDot = Mithril.Reference.Models.Misc.AbilityDynamicDot;
using AbilityDynamicSpecialValue = Mithril.Reference.Models.Misc.AbilityDynamicSpecialValue;
using Quest = Mithril.Reference.Models.Quests.Quest;
using Recipe = Mithril.Reference.Models.Recipes.Recipe;
using PocoSkill = Mithril.Reference.Models.Misc.Skill;
using PocoSourceEnvelope = Mithril.Reference.Models.Sources.SourceEnvelope;
using PocoXpTable = Mithril.Reference.Models.Misc.XpTable;
using SourceModels = Mithril.Reference.Models.Sources;

namespace Mithril.Shared.Reference;

public sealed class ReferenceDataService : IReferenceDataService
{
    public const string CdnRoot = "https://cdn.projectgorgon.com/";
    public const string FallbackCdnVersion = "v469";

    private readonly string _cacheDir;
    private readonly string _bundledDir;
    private readonly HttpClient _http;
    private readonly IDiagnosticsSink? _diag;
    private readonly IPerfTracer? _perf;

    /// <summary>
    /// Map from bundled-file base name (e.g. <c>"quests"</c>) to the
    /// <see cref="IParserSpec"/> that knows how to walk that file's parsed
    /// graph and emit <see cref="UnknownReport"/>s. Cached at construction
    /// from <see cref="ParserRegistry.Discover"/> so per-refresh drift
    /// detection costs nothing in the steady-state (zero unknowns) case.
    /// </summary>
    private readonly IReadOnlyDictionary<string, IParserSpec> _specsByBaseName;

    /// <summary>
    /// Cap on the number of unknown reports logged per file per refresh —
    /// a CDN-shipped flood of unknowns shouldn't drown the diagnostics sink.
    /// First N entries surfaced; remainder summarised with a count.
    /// </summary>
    private const int MaxUnknownReportsPerFile = 5;

    // Items
    private IReadOnlyDictionary<long, Item> _items = new Dictionary<long, Item>();
    private IReadOnlyDictionary<string, Item> _itemsByInternalName =
        new Dictionary<string, Item>(StringComparer.Ordinal);
    private ItemKeywordIndex _keywordIndex = ItemKeywordIndex.Empty;
    private ReferenceFileSnapshot _itemsSnapshot;

    // Recipes
    private IReadOnlyDictionary<string, Recipe> _recipes = new Dictionary<string, Recipe>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, Recipe> _recipesByInternalName =
        new Dictionary<string, Recipe>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<Recipe>> _recipesByProducedItem =
        new Dictionary<string, IReadOnlyList<Recipe>>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<Recipe>> _recipesByIngredientItem =
        new Dictionary<string, IReadOnlyList<Recipe>>(StringComparer.Ordinal);
    // Provenance-retaining sibling of _recipesByIngredientItem (#318 slice 4, surface 1).
    private IReadOnlyDictionary<string, IReadOnlyList<RecipeIngredientItemMatch>> _recipesByIngredientItemWithReason =
        new Dictionary<string, IReadOnlyList<RecipeIngredientItemMatch>>(StringComparer.Ordinal);
    private IReadOnlyCollection<string> _keywordsUsedInRecipeSlots = Array.Empty<string>();
    private IReadOnlyDictionary<string, string> _keywordDisplayNames = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Hardcoded overrides for the slot-walk that builds <see cref="_keywordDisplayNames"/>.
    /// Each entry forces the display name for a keyword tag regardless of what singleton-slot
    /// Descs say. Two semantics:
    /// <list type="bullet">
    ///   <item>Non-null value: use the value as the display name.</item>
    ///   <item><c>null</c> value: suppress the slot-walk entirely for that keyword, so the
    ///   consumer's fallback (typically CamelCase splitting) takes over.</item>
    /// </list>
    /// Seeded with keywords whose singleton-slot Descs encode slot ROLE rather than describing
    /// the keyword itself — e.g. <c>Crystal</c> slots are labelled "Primary Crystal" or
    /// "Auxiliary Crystal" by the recipe author, neither of which is a faithful per-keyword
    /// label. Grow as we discover more cases (small enough to stay hardcoded for now).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string?> KeywordDisplayOverrides
        = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Crystal slots are labelled by their slot role (Primary/Auxiliary) in singleton
            // Descs, not by what a Crystal is. Force fallback.
            ["Crystal"] = null,
            ["MassiveCrystal"] = null,
            // The sole non-tag singleton Desc for Equipment is "Item to Copy Appearance From"
            // (recipe_30105) — recipe-specific, misleading as a generic chip label.
            ["Equipment"] = null,
        };
    private ReferenceFileSnapshot _recipesSnapshot;

    // Skills
    private IReadOnlyDictionary<string, SkillEntry> _skills = new Dictionary<string, SkillEntry>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _skillsSnapshot;

    // XP Tables
    private IReadOnlyDictionary<string, XpTableEntry> _xpTables = new Dictionary<string, XpTableEntry>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _xpTablesSnapshot;

    // NPCs
    private IReadOnlyDictionary<string, NpcEntry> _npcs = new Dictionary<string, NpcEntry>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, PocoNpc> _npcsByInternalName =
        new Dictionary<string, PocoNpc>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<Recipe>> _recipesTaughtByNpc =
        new Dictionary<string, IReadOnlyList<Recipe>>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<Item>> _itemsSoldByNpc =
        new Dictionary<string, IReadOnlyList<Item>>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _npcsSnapshot;

    // Areas (areas.json) — area code → friendly display names.
    private IReadOnlyDictionary<string, AreaEntry> _areas = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<NpcEntry>> _npcsByArea =
        new Dictionary<string, IReadOnlyList<NpcEntry>>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _areasSnapshot;

    // Landmarks (landmarks.json) — area key → in-area landmark list. Mirrors the file's
    // nested dictionary shape; no slim projection (the POCO already carries Name + Desc + Loc
    // + Type + Combo, all of which Area-detail renders directly). Powers #245.
    private IReadOnlyDictionary<string, IReadOnlyList<PocoLandmark>> _landmarks =
        new Dictionary<string, IReadOnlyList<PocoLandmark>>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _landmarksSnapshot;

    // Item sources (sources_items.json)
    private IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> _itemSources =
        new Dictionary<string, IReadOnlyList<ItemSource>>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _itemSourcesSnapshot;

    // Recipe sources (sources_recipes.json)
    private IReadOnlyDictionary<string, IReadOnlyList<RecipeSource>> _recipeSources =
        new Dictionary<string, IReadOnlyList<RecipeSource>>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _recipeSourcesSnapshot;

    // Abilities (abilities.json) — keyed by "ability_N" plus InternalName secondary lookup,
    // with derived skill / upgrade-chain / group / teaching-NPC indices for the Abilities tab
    // and cross-link surfaces (#243).
    private IReadOnlyDictionary<string, Ability> _abilities =
        new Dictionary<string, Ability>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, Ability> _abilitiesByInternalName =
        new Dictionary<string, Ability>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<Ability>> _abilitiesBySkill =
        new Dictionary<string, IReadOnlyList<Ability>>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<Ability>> _abilitiesUpgradingFrom =
        new Dictionary<string, IReadOnlyList<Ability>>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<Ability>> _abilitiesInGroup =
        new Dictionary<string, IReadOnlyList<Ability>>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<Ability>> _abilitiesTaughtByNpc =
        new Dictionary<string, IReadOnlyList<Ability>>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _abilitiesSnapshot;

    // Ability sources (sources_abilities.json)
    private IReadOnlyDictionary<string, IReadOnlyList<AbilitySource>> _abilitySources =
        new Dictionary<string, IReadOnlyList<AbilitySource>>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _abilitySourcesSnapshot;

    // Attributes (attributes.json) — resolves EffectDescs placeholder tokens.
    private IReadOnlyDictionary<string, AttributeEntry> _attributes =
        new Dictionary<string, AttributeEntry>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _attributesSnapshot;

    // Powers (tsysclientinfo.json) — resolves AddItemTSysPower recipe augments.
    private IReadOnlyDictionary<string, PowerEntry> _powers =
        new Dictionary<string, PowerEntry>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _powersSnapshot;

    // Profiles (tsysprofiles.json) — random-roll pools for ExtractTSysPower / TSysCraftedEquipment.
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _profiles =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _profilesSnapshot;

    // Quests (quests.json) — keyed by "quest_N" plus InternalName secondary lookup.
    // Exposes the full Mithril.Reference.Models.Quests.Quest POCO with typed Requirements,
    // Rewards, Objectives etc. for Silmarillion's Quests tab and Gandalf's repeatable-quest
    // timer source.
    private IReadOnlyDictionary<string, Quest> _quests = new Dictionary<string, Quest>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, Quest> _questsByInternalName =
        new Dictionary<string, Quest>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<Quest>> _questsByGiverNpc =
        new Dictionary<string, IReadOnlyList<Quest>>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<Quest>> _questsRewardingItem =
        new Dictionary<string, IReadOnlyList<Quest>>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _questsSnapshot;

    // strings_all.json — flat dictionary of localizable string IDs → display
    // text. Used for friendly-name resolution of chest / cow / tree prefabs
    // (see #178 / Player-Log-Signals wiki).
    private IReadOnlyDictionary<string, string> _strings = new Dictionary<string, string>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _stringsSnapshot;

    // directedgoals.json — flat ordered list (category gates + per-area sub-goals).
    private IReadOnlyList<DirectedGoal> _directedGoals = Array.Empty<DirectedGoal>();
    private ReferenceFileSnapshot _directedGoalsSnapshot;

    // abilitykeywords.json / abilitydynamicdots.json / abilitydynamicspecialvalues.json —
    // flat lists of conditional rules keyed by Req*Keywords predicates. See #288.
    private IReadOnlyList<AbilityKeyword> _abilityKeywordRules = Array.Empty<AbilityKeyword>();
    private ReferenceFileSnapshot _abilityKeywordRulesSnapshot;
    private IReadOnlyList<AbilityDynamicDot> _abilityDynamicDots = Array.Empty<AbilityDynamicDot>();
    private ReferenceFileSnapshot _abilityDynamicDotsSnapshot;
    private IReadOnlyList<AbilityDynamicSpecialValue> _abilityDynamicSpecialValues = Array.Empty<AbilityDynamicSpecialValue>();
    private ReferenceFileSnapshot _abilityDynamicSpecialValuesSnapshot;

    // Effects (effects.json) — keyed by "effect_N" plus a sibling InternalName lookup
    // (envelope key === InternalName, lifted by ParseEffects). Derived keyword and
    // stacking indices plus the bidirectional ability×effect-keyword cross-link indices
    // power Silmarillion's Effects tab.
    private IReadOnlyDictionary<string, Effect> _effects =
        new Dictionary<string, Effect>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, Effect> _effectsByInternalName =
        new Dictionary<string, Effect>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<Effect>> _effectsByKeyword =
        new Dictionary<string, IReadOnlyList<Effect>>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<Effect>> _effectsByStackingType =
        new Dictionary<string, IReadOnlyList<Effect>>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<EffectAbilityMatch>> _abilitiesByEffectKeyword =
        new Dictionary<string, IReadOnlyList<EffectAbilityMatch>>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<Effect>> _effectsByTriggeringAbilityKeyword =
        new Dictionary<string, IReadOnlyList<Effect>>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _effectsSnapshot;

    // Lorebooks (lorebooks.json) — keyed by "Book_N" envelope key, plus InternalName and
    // numeric-id secondary lookups. lorebookinfo.json is a separate sidecar carrying the
    // 7 category display-metadata entries. ItemsBestowingLorebook is the #318 popup-from-
    // index source for the "Items that bestow this book" surface (#247).
    private IReadOnlyDictionary<string, Lorebook> _lorebooks =
        new Dictionary<string, Lorebook>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, Lorebook> _lorebooksByInternalName =
        new Dictionary<string, Lorebook>(StringComparer.Ordinal);
    private IReadOnlyDictionary<int, Lorebook> _lorebooksById =
        new Dictionary<int, Lorebook>();
    private IReadOnlyDictionary<string, IReadOnlyList<Item>> _itemsBestowingLorebook =
        new Dictionary<string, IReadOnlyList<Item>>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _lorebooksSnapshot;
    private IReadOnlyDictionary<string, LorebookCategoryInfo> _lorebookCategories =
        new Dictionary<string, LorebookCategoryInfo>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _lorebookInfoSnapshot;

    public ReferenceDataService(string cacheDir, HttpClient http, IDiagnosticsSink? diag = null, string? bundledDir = null, IPerfTracer? perf = null)
    {
        _cacheDir = cacheDir;
        _http = http;
        _diag = diag;
        _perf = perf;
        _bundledDir = bundledDir ?? Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");

        _specsByBaseName = ParserRegistry.Discover()
            .ToDictionary(
                s => Path.GetFileNameWithoutExtension(s.FileName),
                StringComparer.Ordinal);

        _itemsSnapshot = new ReferenceFileSnapshot("items", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _recipesSnapshot = new ReferenceFileSnapshot("recipes", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _skillsSnapshot = new ReferenceFileSnapshot("skills", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _xpTablesSnapshot = new ReferenceFileSnapshot("xptables", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _npcsSnapshot = new ReferenceFileSnapshot("npcs", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _areasSnapshot = new ReferenceFileSnapshot("areas", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _landmarksSnapshot = new ReferenceFileSnapshot("landmarks", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _itemSourcesSnapshot = new ReferenceFileSnapshot("sources_items", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _recipeSourcesSnapshot = new ReferenceFileSnapshot("sources_recipes", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _abilitiesSnapshot = new ReferenceFileSnapshot("abilities", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _abilitySourcesSnapshot = new ReferenceFileSnapshot("sources_abilities", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _attributesSnapshot = new ReferenceFileSnapshot("attributes", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _powersSnapshot = new ReferenceFileSnapshot("tsysclientinfo", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _profilesSnapshot = new ReferenceFileSnapshot("tsysprofiles", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _questsSnapshot = new ReferenceFileSnapshot("quests", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _stringsSnapshot = new ReferenceFileSnapshot("strings_all", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _directedGoalsSnapshot = new ReferenceFileSnapshot("directedgoals", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _abilityKeywordRulesSnapshot = new ReferenceFileSnapshot("abilitykeywords", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _abilityDynamicDotsSnapshot = new ReferenceFileSnapshot("abilitydynamicdots", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _abilityDynamicSpecialValuesSnapshot = new ReferenceFileSnapshot("abilitydynamicspecialvalues", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _effectsSnapshot = new ReferenceFileSnapshot("effects", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _lorebooksSnapshot = new ReferenceFileSnapshot("lorebooks", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _lorebookInfoSnapshot = new ReferenceFileSnapshot("lorebookinfo", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);

        LoadItems();
        LoadRecipes();
        LoadSkills();
        LoadXpTables();
        LoadNpcs();
        LoadAreas();
        LoadLandmarks();           // After LoadNpcs / LoadAreas so the area-key set is final; landmark parse uses the same area-key shape.
        LoadQuests();              // Must run before LoadItemSources / LoadRecipeSources / LoadAbilitySources — ResolveSourceContext reads _quests.
        LoadItemSources();
        LoadRecipeSources();
        LoadAbilities();           // Must run before LoadAbilitySources — ParseAndSwapAbilitySources keys by Ability.InternalName.
                                   // Also must run before LoadEffects so BuildEffectAbilityCrossLinkIndices sees both sides.
        LoadAbilitySources();
        LoadAttributes();
        LoadPowers();
        LoadProfiles();
        LoadStrings();
        LoadDirectedGoals();
        LoadAbilityKeywords();
        LoadAbilityDynamicDots();
        LoadAbilityDynamicSpecialValues();
        LoadEffects();
        LoadLorebookInfo();        // Sidecar; independent of lorebooks.json. Load before
                                   // LoadLorebooks is not required (categories aren't a parse
                                   // dependency) but keeps the lorebook pair adjacent.
        LoadLorebooks();           // After LoadItems so BuildLorebookItemCrossLinkIndex sees
                                   // the final item set on first build.
    }

    public IReadOnlyList<string> Keys { get; } = ["items", "recipes", "skills", "xptables", "npcs", "areas", "landmarks", "sources_items", "sources_recipes", "abilities", "sources_abilities", "attributes", "tsysclientinfo", "tsysprofiles", "quests", "strings_all", "directedgoals", "abilitykeywords", "abilitydynamicdots", "abilitydynamicspecialvalues", "effects", "lorebooks", "lorebookinfo"];

    public IReadOnlyDictionary<long, Item> Items => _items;
    public IReadOnlyDictionary<string, Item> ItemsByInternalName => _itemsByInternalName;
    public ItemKeywordIndex KeywordIndex => _keywordIndex;
    public IReadOnlyDictionary<string, Recipe> Recipes => _recipes;
    public IReadOnlyDictionary<string, Recipe> RecipesByInternalName => _recipesByInternalName;
    public IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesByProducedItem => _recipesByProducedItem;
    public IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesByIngredientItem => _recipesByIngredientItem;
    public IReadOnlyDictionary<string, IReadOnlyList<RecipeIngredientItemMatch>> RecipesByIngredientItemWithReason => _recipesByIngredientItemWithReason;
    public IReadOnlyCollection<string> KeywordsUsedInRecipeSlots => _keywordsUsedInRecipeSlots;
    public IReadOnlyDictionary<string, string> KeywordDisplayNames => _keywordDisplayNames;
    public IReadOnlyDictionary<string, SkillEntry> Skills => _skills;
    public IReadOnlyDictionary<string, XpTableEntry> XpTables => _xpTables;
    public IReadOnlyDictionary<string, NpcEntry> Npcs => _npcs;
    public IReadOnlyDictionary<string, PocoNpc> NpcsByInternalName => _npcsByInternalName;
    public IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesTaughtByNpc => _recipesTaughtByNpc;
    public IReadOnlyDictionary<string, IReadOnlyList<Item>> ItemsSoldByNpc => _itemsSoldByNpc;
    public IReadOnlyDictionary<string, AreaEntry> Areas => _areas;
    public IReadOnlyDictionary<string, IReadOnlyList<PocoLandmark>> Landmarks => _landmarks;
    public IReadOnlyDictionary<string, IReadOnlyList<NpcEntry>> NpcsByArea => _npcsByArea;
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources => _itemSources;
    public IReadOnlyDictionary<string, IReadOnlyList<RecipeSource>> RecipeSources => _recipeSources;
    public IReadOnlyDictionary<string, Ability> Abilities => _abilities;
    public IReadOnlyDictionary<string, Ability> AbilitiesByInternalName => _abilitiesByInternalName;
    public IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesBySkill => _abilitiesBySkill;
    public IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesUpgradingFrom => _abilitiesUpgradingFrom;
    public IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesInGroup => _abilitiesInGroup;
    public IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesTaughtByNpc => _abilitiesTaughtByNpc;
    public IReadOnlyDictionary<string, IReadOnlyList<AbilitySource>> AbilitySources => _abilitySources;
    public IReadOnlyDictionary<string, AttributeEntry> Attributes => _attributes;
    public IReadOnlyDictionary<string, PowerEntry> Powers => _powers;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles => _profiles;
    public IReadOnlyDictionary<string, Quest> Quests => _quests;
    public IReadOnlyDictionary<string, Quest> QuestsByInternalName => _questsByInternalName;
    public IReadOnlyDictionary<string, IReadOnlyList<Quest>> QuestsByGiverNpc => _questsByGiverNpc;
    public IReadOnlyDictionary<string, IReadOnlyList<Quest>> QuestsRewardingItem => _questsRewardingItem;
    public IReadOnlyList<DirectedGoal> DirectedGoals => _directedGoals;
    public IReadOnlyList<AbilityKeyword> AbilityKeywordRules => _abilityKeywordRules;
    public IReadOnlyList<AbilityDynamicDot> AbilityDynamicDots => _abilityDynamicDots;
    public IReadOnlyList<AbilityDynamicSpecialValue> AbilityDynamicSpecialValues => _abilityDynamicSpecialValues;
    public IReadOnlyDictionary<string, Effect> Effects => _effects;
    public IReadOnlyDictionary<string, Effect> EffectsByInternalName => _effectsByInternalName;
    public IReadOnlyDictionary<string, IReadOnlyList<Effect>> EffectsByKeyword => _effectsByKeyword;
    public IReadOnlyDictionary<string, IReadOnlyList<Effect>> EffectsByStackingType => _effectsByStackingType;
    public IReadOnlyDictionary<string, IReadOnlyList<EffectAbilityMatch>> AbilitiesByEffectKeyword => _abilitiesByEffectKeyword;
    public IReadOnlyDictionary<string, IReadOnlyList<Effect>> EffectsByTriggeringAbilityKeyword => _effectsByTriggeringAbilityKeyword;
    public IReadOnlyDictionary<string, Lorebook> Lorebooks => _lorebooks;
    public IReadOnlyDictionary<string, Lorebook> LorebooksByInternalName => _lorebooksByInternalName;
    public IReadOnlyDictionary<int, Lorebook> LorebooksById => _lorebooksById;
    public IReadOnlyDictionary<string, IReadOnlyList<Item>> ItemsBestowingLorebook => _itemsBestowingLorebook;
    public IReadOnlyDictionary<string, LorebookCategoryInfo> LorebookCategories => _lorebookCategories;
    public IReadOnlyDictionary<string, string> Strings => _strings;

    public ReferenceFileSnapshot GetSnapshot(string key) => key switch
    {
        "items" => _itemsSnapshot,
        "recipes" => _recipesSnapshot,
        "skills" => _skillsSnapshot,
        "xptables" => _xpTablesSnapshot,
        "npcs" => _npcsSnapshot,
        "areas" => _areasSnapshot,
        "landmarks" => _landmarksSnapshot,
        "sources_items" => _itemSourcesSnapshot,
        "sources_recipes" => _recipeSourcesSnapshot,
        "abilities" => _abilitiesSnapshot,
        "sources_abilities" => _abilitySourcesSnapshot,
        "attributes" => _attributesSnapshot,
        "tsysclientinfo" => _powersSnapshot,
        "tsysprofiles" => _profilesSnapshot,
        "quests" => _questsSnapshot,
        "strings_all" => _stringsSnapshot,
        "directedgoals" => _directedGoalsSnapshot,
        "abilitykeywords" => _abilityKeywordRulesSnapshot,
        "abilitydynamicdots" => _abilityDynamicDotsSnapshot,
        "abilitydynamicspecialvalues" => _abilityDynamicSpecialValuesSnapshot,
        "effects" => _effectsSnapshot,
        "lorebooks" => _lorebooksSnapshot,
        "lorebookinfo" => _lorebookInfoSnapshot,
        _ => throw new ArgumentException($"Unknown reference file key: {key}", nameof(key)),
    };

    public event EventHandler<string>? FileUpdated;

    public Task RefreshAsync(string key, CancellationToken ct = default) => key switch
    {
        "items" => RefreshFileAsync("items", ReferenceDeserializer.ParseItems, ParseAndSwapItems, ct),
        "recipes" => RefreshFileAsync("recipes", ReferenceDeserializer.ParseRecipes, ParseAndSwapRecipes, ct),
        "skills" => RefreshFileAsync("skills", ReferenceDeserializer.ParseSkills, ParseAndSwapSkills, ct),
        "xptables" => RefreshFileAsync("xptables", ReferenceDeserializer.ParseXpTables, ParseAndSwapXpTables, ct),
        "npcs" => RefreshFileAsync("npcs", ReferenceDeserializer.ParseNpcs, ParseAndSwapNpcs, ct),
        "areas" => RefreshFileAsync("areas", ReferenceDeserializer.ParseAreas, ParseAndSwapAreas, ct),
        "landmarks" => RefreshFileAsync("landmarks", ReferenceDeserializer.ParseLandmarks, ParseAndSwapLandmarks, ct),
        "sources_items" => RefreshFileAsync("sources_items", ReferenceDeserializer.ParseSources, ParseAndSwapItemSources, ct),
        "sources_recipes" => RefreshFileAsync("sources_recipes", ReferenceDeserializer.ParseSources, ParseAndSwapRecipeSources, ct),
        "abilities" => RefreshFileAsync("abilities", ReferenceDeserializer.ParseAbilities, ParseAndSwapAbilities, ct),
        "sources_abilities" => RefreshFileAsync("sources_abilities", ReferenceDeserializer.ParseSources, ParseAndSwapAbilitySources, ct),
        "attributes" => RefreshFileAsync("attributes", ReferenceDeserializer.ParseAttributes, ParseAndSwapAttributes, ct),
        "tsysclientinfo" => RefreshFileAsync("tsysclientinfo", ReferenceDeserializer.ParseTsysClientInfo, ParseAndSwapPowers, ct),
        "tsysprofiles" => RefreshFileAsync("tsysprofiles", ReferenceDeserializer.ParseTsysProfiles, ParseAndSwapProfiles, ct),
        "quests" => RefreshFileAsync("quests", ReferenceDeserializer.ParseQuests, ParseAndSwapQuests, ct),
        "strings_all" => RefreshFileAsync("strings_all", ReferenceDeserializer.ParseStringsAll, ParseAndSwapStrings, ct),
        "directedgoals" => RefreshFileAsync("directedgoals", ReferenceDeserializer.ParseDirectedGoals, ParseAndSwapDirectedGoals, ct),
        "abilitykeywords" => RefreshFileAsync("abilitykeywords", ReferenceDeserializer.ParseAbilityKeywords, ParseAndSwapAbilityKeywords, ct),
        "abilitydynamicdots" => RefreshFileAsync("abilitydynamicdots", ReferenceDeserializer.ParseAbilityDynamicDots, ParseAndSwapAbilityDynamicDots, ct),
        "abilitydynamicspecialvalues" => RefreshFileAsync("abilitydynamicspecialvalues", ReferenceDeserializer.ParseAbilityDynamicSpecialValues, ParseAndSwapAbilityDynamicSpecialValues, ct),
        "effects" => RefreshFileAsync("effects", ReferenceDeserializer.ParseEffects, ParseAndSwapEffects, ct),
        "lorebooks" => RefreshFileAsync("lorebooks", ReferenceDeserializer.ParseLorebooks, ParseAndSwapLorebooks, ct),
        "lorebookinfo" => RefreshFileAsync("lorebookinfo", ReferenceDeserializer.ParseLorebookInfo, ParseAndSwapLorebookInfo, ct),
        _ => throw new ArgumentException($"Unknown reference file key: {key}", nameof(key)),
    };

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await RefreshAsync("items", ct).ConfigureAwait(false);
        await RefreshAsync("recipes", ct).ConfigureAwait(false);
        await RefreshAsync("skills", ct).ConfigureAwait(false);
        await RefreshAsync("xptables", ct).ConfigureAwait(false);
        await RefreshAsync("npcs", ct).ConfigureAwait(false);
        await RefreshAsync("areas", ct).ConfigureAwait(false);
        await RefreshAsync("landmarks", ct).ConfigureAwait(false);
        await RefreshAsync("sources_items", ct).ConfigureAwait(false);
        await RefreshAsync("sources_recipes", ct).ConfigureAwait(false);
        await RefreshAsync("abilities", ct).ConfigureAwait(false);
        await RefreshAsync("sources_abilities", ct).ConfigureAwait(false);
        await RefreshAsync("attributes", ct).ConfigureAwait(false);
        await RefreshAsync("tsysclientinfo", ct).ConfigureAwait(false);
        await RefreshAsync("tsysprofiles", ct).ConfigureAwait(false);
        await RefreshAsync("quests", ct).ConfigureAwait(false);
        await RefreshAsync("strings_all", ct).ConfigureAwait(false);
        await RefreshAsync("directedgoals", ct).ConfigureAwait(false);
        await RefreshAsync("abilitykeywords", ct).ConfigureAwait(false);
        await RefreshAsync("abilitydynamicdots", ct).ConfigureAwait(false);
        await RefreshAsync("abilitydynamicspecialvalues", ct).ConfigureAwait(false);
        await RefreshAsync("effects", ct).ConfigureAwait(false);
        await RefreshAsync("lorebookinfo", ct).ConfigureAwait(false);
        await RefreshAsync("lorebooks", ct).ConfigureAwait(false);
    }

    public void BeginBackgroundRefresh()
    {
        _ = Task.Run(async () =>
        {
            try { await RefreshAllAsync(CancellationToken.None); }
            catch (Exception ex) { _diag?.Warn("Reference", $"Background refresh failed: {ex.Message}"); }
        });
    }

    // ── Generic load/refresh helpers ──────────────────────────────────────

    /// <summary>
    /// Loads <paramref name="fileName"/> from the on-disk cache (preferring the latest
    /// CDN copy if present) or falls back to the bundled JSON shipped with the app.
    /// The JSON content is parsed via <paramref name="parser"/> — typically a
    /// <see cref="ReferenceDeserializer"/> entry point — and the resulting POCO graph
    /// is handed to <paramref name="swapper"/> for projection into the per-file
    /// <see cref="*Entry"/> dictionaries this service exposes.
    /// </summary>
    private void LoadFile<T>(
        string fileName,
        Func<string, T> parser,
        Action<T, ReferenceFileMetadata> swapper)
    {
        var cachePath = Path.Combine(_cacheDir, $"{fileName}.json");
        var cacheMetaPath = Path.Combine(_cacheDir, $"{fileName}.meta.json");

        if (File.Exists(cachePath))
        {
            try
            {
                var meta = TryLoadMetadata(cacheMetaPath, ReferenceFileSource.Cache);
                var json = File.ReadAllText(cachePath);
                var parsed = parser(json);
                swapper(parsed, meta);
                ReportUnknowns(fileName, parsed!, meta.CdnVersion);
                _diag?.Info("Reference", $"Loaded {fileName} from cache ({meta.CdnVersion}).");
                return;
            }
            catch (Exception ex)
            {
                _diag?.Warn("Reference", $"{fileName} cache load failed, falling back to bundled: {ex.Message}");
            }
        }

        var bundledPath = Path.Combine(_bundledDir, $"{fileName}.json");
        var bundledMetaPath = Path.Combine(_bundledDir, $"{fileName}.meta.json");
        if (!File.Exists(bundledPath))
        {
            _diag?.Warn("Reference", $"Bundled {fileName}.json missing at {bundledPath}.");
            return;
        }
        var bundledMeta = TryLoadMetadata(bundledMetaPath, ReferenceFileSource.Bundled);
        var bundledJson = File.ReadAllText(bundledPath);
        var bundledParsed = parser(bundledJson);
        swapper(bundledParsed, bundledMeta);
        ReportUnknowns(fileName, bundledParsed!, bundledMeta.CdnVersion);
        _diag?.Info("Reference", $"Loaded {fileName} from bundled ({bundledMeta.CdnVersion}).");
    }

    private async Task RefreshFileAsync<T>(
        string fileName,
        Func<string, T> parser,
        Action<T, ReferenceFileMetadata> swapper,
        CancellationToken ct)
    {
        var version = await CdnVersionDetector.TryDetectAsync(_http, CdnRoot, ct).ConfigureAwait(false)
                      ?? GetSnapshot(fileName).CdnVersion
                      ?? FallbackCdnVersion;
        var url = $"{CdnRoot}{version}/data/{fileName}.json";
        _diag?.Info("Reference", $"Refreshing {fileName} from {url}.");

        byte[] body;
        var fetchSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Reference", $"{fileName}.json fetch failed ({ex.Message}); keeping existing data.");
            _perf?.EmitRefFetch(fileName, cacheHit: false, durationMs: fetchSw.Elapsed.TotalMilliseconds, bytes: 0);
            return;
        }
        _perf?.EmitRefFetch(fileName, cacheHit: false, durationMs: fetchSw.Elapsed.TotalMilliseconds, bytes: body.LongLength);

        var meta = new ReferenceFileMetadata
        {
            CdnVersion = version,
            FetchedAtUtc = DateTimeOffset.UtcNow,
            Source = ReferenceFileSource.Cdn,
        };

        Directory.CreateDirectory(_cacheDir);
        var cachePath = Path.Combine(_cacheDir, $"{fileName}.json");
        var metaPath = Path.Combine(_cacheDir, $"{fileName}.meta.json");
        await Settings.AtomicFile.WriteAllBytesAtomicAsync(cachePath, body, ct).ConfigureAwait(false);
        await Settings.AtomicFile.WriteJsonAtomicAsync(metaPath, meta,
            ReferenceJsonContext.Default.ReferenceFileMetadata, ct).ConfigureAwait(false);

        // Parse + dictionary swap + drift walk are CPU-bound and collectively
        // 500–1380 ms per file at full CDN payload (strings_all = 15 MB).
        // Force them onto the thread pool — when this method runs under a
        // captured sync context (e.g. the WPF dispatcher via the Settings
        // panel's Refresh All button) those costs MUST NOT land on the UI
        // thread. The swap mutates reference-typed snapshot fields with
        // atomic reference assignment; readers tolerate seeing either the
        // old or new dictionary. See #197.
        await Task.Run(() =>
        {
            var json = Encoding.UTF8.GetString(body);
            var parsed = parser(json);
            swapper(parsed, meta);
            ReportUnknowns(fileName, parsed!, meta.CdnVersion);
        }, ct).ConfigureAwait(false);
        _diag?.Info("Reference", $"{fileName}.json refreshed: version {version}.");
        FileUpdated?.Invoke(this, fileName);
    }

    /// <summary>
    /// Walks the freshly-parsed graph for any <see cref="Mithril.Reference.Models.IUnknownDiscriminator"/>
    /// sentinels and emits a diagnostics warning per finding (capped at
    /// <see cref="MaxUnknownReportsPerFile"/>). The bundled JSON is validated
    /// to have zero unknowns by <c>BundledDataValidationTests</c>; any
    /// warning here therefore means the live CDN has shipped a discriminator
    /// value the model layer hasn't been updated to recognise — that's the
    /// schema-drift alarm.
    /// </summary>
    private void ReportUnknowns(string fileName, object parsed, string cdnVersion)
    {
        if (_diag is null) return;
        if (!_specsByBaseName.TryGetValue(fileName, out var spec)) return;

        IList<UnknownReport> reports;
        try
        {
            reports = spec.EnumerateUnknowns(parsed).Take(MaxUnknownReportsPerFile + 1).ToList();
        }
        catch (Exception ex)
        {
            _diag.Warn("Reference", $"{fileName} unknown-walk threw: {ex.Message}");
            return;
        }

        if (reports.Count == 0) return;

        var truncated = reports.Count > MaxUnknownReportsPerFile;
        var visible = truncated ? reports.Take(MaxUnknownReportsPerFile) : reports;
        foreach (var u in visible)
            _diag.Warn(
                "Reference",
                $"{fileName} (v{cdnVersion}): unknown {u.BaseTypeName} discriminator '{u.DiscriminatorValue}' at {u.Path}");

        if (truncated)
            _diag.Warn(
                "Reference",
                $"{fileName} (v{cdnVersion}): additional unknowns truncated; first {MaxUnknownReportsPerFile} reported.");
    }

    // ── Per-type load entry points ───────────────────────────────────────

    private void LoadItems() => LoadFile("items", ReferenceDeserializer.ParseItems, ParseAndSwapItems);
    private void LoadRecipes() => LoadFile("recipes", ReferenceDeserializer.ParseRecipes, ParseAndSwapRecipes);
    private void LoadSkills() => LoadFile("skills", ReferenceDeserializer.ParseSkills, ParseAndSwapSkills);
    private void LoadXpTables() => LoadFile("xptables", ReferenceDeserializer.ParseXpTables, ParseAndSwapXpTables);
    private void LoadNpcs() => LoadFile("npcs", ReferenceDeserializer.ParseNpcs, ParseAndSwapNpcs);
    private void LoadAreas() => LoadFile("areas", ReferenceDeserializer.ParseAreas, ParseAndSwapAreas);
    private void LoadLandmarks() => LoadFile("landmarks", ReferenceDeserializer.ParseLandmarks, ParseAndSwapLandmarks);
    private void LoadItemSources() => LoadFile("sources_items", ReferenceDeserializer.ParseSources, ParseAndSwapItemSources);
    private void LoadRecipeSources() => LoadFile("sources_recipes", ReferenceDeserializer.ParseSources, ParseAndSwapRecipeSources);
    private void LoadAbilities() => LoadFile("abilities", ReferenceDeserializer.ParseAbilities, ParseAndSwapAbilities);
    private void LoadAbilitySources() => LoadFile("sources_abilities", ReferenceDeserializer.ParseSources, ParseAndSwapAbilitySources);
    private void LoadAttributes() => LoadFile("attributes", ReferenceDeserializer.ParseAttributes, ParseAndSwapAttributes);
    private void LoadPowers() => LoadFile("tsysclientinfo", ReferenceDeserializer.ParseTsysClientInfo, ParseAndSwapPowers);
    private void LoadProfiles() => LoadFile("tsysprofiles", ReferenceDeserializer.ParseTsysProfiles, ParseAndSwapProfiles);
    private void LoadQuests() => LoadFile("quests", ReferenceDeserializer.ParseQuests, ParseAndSwapQuests);
    private void LoadStrings() => LoadFile("strings_all", ReferenceDeserializer.ParseStringsAll, ParseAndSwapStrings);
    private void LoadDirectedGoals() => LoadFile("directedgoals", ReferenceDeserializer.ParseDirectedGoals, ParseAndSwapDirectedGoals);
    private void LoadAbilityKeywords() => LoadFile("abilitykeywords", ReferenceDeserializer.ParseAbilityKeywords, ParseAndSwapAbilityKeywords);
    private void LoadAbilityDynamicDots() => LoadFile("abilitydynamicdots", ReferenceDeserializer.ParseAbilityDynamicDots, ParseAndSwapAbilityDynamicDots);
    private void LoadAbilityDynamicSpecialValues() => LoadFile("abilitydynamicspecialvalues", ReferenceDeserializer.ParseAbilityDynamicSpecialValues, ParseAndSwapAbilityDynamicSpecialValues);
    private void LoadEffects() => LoadFile("effects", ReferenceDeserializer.ParseEffects, ParseAndSwapEffects);
    private void LoadLorebooks() => LoadFile("lorebooks", ReferenceDeserializer.ParseLorebooks, ParseAndSwapLorebooks);
    private void LoadLorebookInfo() => LoadFile("lorebookinfo", ReferenceDeserializer.ParseLorebookInfo, ParseAndSwapLorebookInfo);

    // ── Per-type parse-and-swap ──────────────────────────────────────────

    private void ParseAndSwapItems(IReadOnlyDictionary<string, Item> raw, ReferenceFileMetadata meta)
    {
        var byId = new Dictionary<long, Item>(raw.Count);
        var byName = new Dictionary<string, Item>(raw.Count, StringComparer.Ordinal);
        foreach (var pair in raw)
        {
            var v = pair.Value;
            // ParseItems already lifts the numeric id from "item_5010" → v.Id.
            // Skip entries with no resolvable id (keys not shaped as "<prefix>_<n>").
            if (v.Id == 0) continue;

            // Normalise away null/empty TSysProfile so consumers can treat present
            // strings as load-bearing — matches the slim path's behaviour.
            if (string.IsNullOrEmpty(v.TSysProfile)) v.TSysProfile = null;

            byId[v.Id] = v;
            if (!string.IsNullOrEmpty(v.InternalName)) byName[v.InternalName!] = v;
        }
        _items = byId;
        _itemsByInternalName = byName;
        _keywordIndex = new ItemKeywordIndex(byId);
        _itemsSnapshot = new ReferenceFileSnapshot("items", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byId.Count);
        BuildRecipeCrossLinkIndices();
        BuildNpcCrossLinkIndices();
        BuildQuestCrossLinkIndices();
        BuildLorebookItemCrossLinkIndex();
    }

    private void ParseAndSwapRecipes(IReadOnlyDictionary<string, Recipe> raw, ReferenceFileMetadata meta)
    {
        var byKey = new Dictionary<string, Recipe>(raw.Count, StringComparer.Ordinal);
        var byName = new Dictionary<string, Recipe>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, v) in raw)
        {
            // ParseRecipes already lifts the envelope key onto Recipe.Key.
            byKey[key] = v;
            if (!string.IsNullOrEmpty(v.InternalName)) byName[v.InternalName!] = v;
        }
        _recipes = byKey;
        _recipesByInternalName = byName;
        _recipesSnapshot = new ReferenceFileSnapshot("recipes", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byKey.Count);
        BuildRecipeCrossLinkIndices();
        BuildNpcCrossLinkIndices();
    }

    /// <summary>
    /// Builds <see cref="_recipesByProducedItem"/>, <see cref="_recipesByIngredientItem"/>,
    /// <see cref="_keywordsUsedInRecipeSlots"/>, and <see cref="_keywordDisplayNames"/>
    /// from the current <see cref="_recipes"/> + <see cref="_items"/>. Items lacking
    /// InternalName or item codes that don't resolve to a known item are silently skipped
    /// (they can't be cross-linked to a browsable entity anyway).
    /// <see cref="_keywordsUsedInRecipeSlots"/> accumulates the union of every
    /// <see cref="Mithril.Reference.Models.Recipes.RecipeKeywordIngredient.ItemKeys"/> entry
    /// across all recipes. <see cref="_keywordDisplayNames"/> records friendly per-keyword
    /// display strings — checked against <see cref="KeywordDisplayOverrides"/> first, then
    /// resolved by picking the most-common Desc across all singleton slots that reference
    /// the keyword (with <c>strings_all</c> consulted before <see cref="RecipeKeywordIngredient.Desc"/>).
    /// Called from both ParseAndSwapItems and ParseAndSwapRecipes so a refresh of either file
    /// rebuilds the indices.
    /// </summary>
    private void BuildRecipeCrossLinkIndices()
    {
        var produced = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);
        var ingredient = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);
        var keywordSet = new HashSet<string>(StringComparer.Ordinal);
        // tag → Desc → occurrence count. Used after the walk to pick the most-common Desc
        // for each keyword. Most-common-wins handles cases where one Desc dominates across
        // recipes; ties broken by first-encountered (dictionary insertion order is stable).
        var descCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        foreach (var recipe in _recipes.Values)
        {
            var resultSource = (recipe.ResultItems is { Count: > 0 } ? recipe.ResultItems : recipe.ProtoResultItems)
                ?? (IReadOnlyList<Mithril.Reference.Models.Recipes.RecipeResultItem>)Array.Empty<Mithril.Reference.Models.Recipes.RecipeResultItem>();
            foreach (var result in resultSource)
            {
                if (!_items.TryGetValue(result.ItemCode, out var item) || string.IsNullOrEmpty(item.InternalName))
                    continue;
                if (!produced.TryGetValue(item.InternalName, out var list))
                {
                    list = new List<Recipe>();
                    produced[item.InternalName] = list;
                }
                if (!list.Contains(recipe))
                    list.Add(recipe);
            }

            // recipe.Ingredients is annotated non-nullable but JSON deserialization with a missing
            // field yields null at runtime — guard rather than crash.
            var ingredients = recipe.Ingredients ?? (IReadOnlyList<Mithril.Reference.Models.Recipes.RecipeIngredient>)Array.Empty<Mithril.Reference.Models.Recipes.RecipeIngredient>();
            for (var slotIndex = 0; slotIndex < ingredients.Count; slotIndex++)
            {
                var ing = ingredients[slotIndex];
                switch (ing)
                {
                    case Mithril.Reference.Models.Recipes.RecipeItemIngredient itemIng:
                        if (_items.TryGetValue(itemIng.ItemCode, out var item) && !string.IsNullOrEmpty(item.InternalName))
                            AddIngredientRecipe(ingredient, item.InternalName, recipe);
                        break;

                    case Mithril.Reference.Models.Recipes.RecipeKeywordIngredient kwIng:
                        foreach (var key in kwIng.ItemKeys)
                            keywordSet.Add(key);
                        // Only singleton slots feed display-name resolution. Composite tuples
                        // (e.g. ["EquipmentSlot:MainHand","MinTSysPrereq:0"]) carry Descs that
                        // describe the AND-matched composite, not any one tag.
                        if (kwIng.ItemKeys.Count == 1)
                        {
                            var tag = kwIng.ItemKeys[0];
                            var resolved = ResolveSlotDesc(recipe.Key, slotIndex, kwIng.Desc);
                            if (!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, tag, StringComparison.Ordinal))
                            {
                                if (!descCounts.TryGetValue(tag, out var perDesc))
                                {
                                    perDesc = new Dictionary<string, int>(StringComparer.Ordinal);
                                    descCounts[tag] = perDesc;
                                }
                                perDesc[resolved] = perDesc.TryGetValue(resolved, out var c) ? c + 1 : 1;
                            }
                        }
                        break;
                }
            }
        }

        // Resolve final display names: overrides win first, then most-common-Desc from the walk.
        var displayNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (tag, perDesc) in descCounts)
        {
            if (KeywordDisplayOverrides.TryGetValue(tag, out var overrideValue))
            {
                if (!string.IsNullOrEmpty(overrideValue))
                    displayNames[tag] = overrideValue;
                // null override → suppress; let the consumer's fallback take over
                continue;
            }
            // Pick the Desc with the highest occurrence count. Dictionary iteration order is
            // insertion order, so ties resolve to the first-encountered Desc deterministically.
            string? best = null;
            var bestCount = 0;
            foreach (var (desc, count) in perDesc)
            {
                if (count > bestCount)
                {
                    best = desc;
                    bestCount = count;
                }
            }
            if (best is not null)
                displayNames[tag] = best;
        }
        // Apply non-null overrides for tags that didn't appear in any singleton slot at all.
        foreach (var (tag, overrideValue) in KeywordDisplayOverrides)
        {
            if (!string.IsNullOrEmpty(overrideValue) && !displayNames.ContainsKey(tag))
                displayNames[tag] = overrideValue;
        }

        _recipesByProducedItem = produced.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Recipe>)kv.Value, StringComparer.Ordinal);
        _recipesByIngredientItem = ingredient.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Recipe>)kv.Value, StringComparer.Ordinal);
        // Provenance-retaining sibling (#318 slice 4, surface 1 — Items "Used in"). The
        // relationship is single-reason: AddIngredientRecipe only ever added direct
        // RecipeItemIngredient matches (keyword slots feed keywordSet, a separate surface),
        // and the per-item list is already dedup'd by recipe — so every member is exactly
        // one (recipe, DirectIngredient) record and a distinct-member count equals the
        // displayed "View all N". Derived from the same `ingredient` accumulation so the
        // two indices cannot diverge (single materialization — the #318 invariant).
        _recipesByIngredientItemWithReason = ingredient.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<RecipeIngredientItemMatch>)kv.Value
                .Select(r => new RecipeIngredientItemMatch(r, RecipeIngredientItemMatchReason.DirectIngredient))
                .ToList(),
            StringComparer.Ordinal);
        _keywordsUsedInRecipeSlots = keywordSet;
        _keywordDisplayNames = displayNames;

        static void AddIngredientRecipe(Dictionary<string, List<Recipe>> map, string internalName, Recipe recipe)
        {
            if (!map.TryGetValue(internalName, out var list))
            {
                list = new List<Recipe>();
                map[internalName] = list;
            }
            if (!list.Contains(recipe))
                list.Add(recipe);
        }

        string? ResolveSlotDesc(string recipeKey, int slotIndex, string? fallback)
        {
            var stringsKey = $"{recipeKey}_Ingredients_{slotIndex}_Desc";
            return _strings.TryGetValue(stringsKey, out var s) && !string.IsNullOrEmpty(s)
                ? s
                : fallback;
        }
    }

    /// <summary>
    /// Builds <see cref="_recipesTaughtByNpc"/> and <see cref="_itemsSoldByNpc"/> from the
    /// current <see cref="_recipeSources"/> / <see cref="_itemSources"/> filtered to the
    /// <c>Training</c> / <c>Vendor</c> source kinds. Powers the Silmarillion NPCs tab's
    /// "Teaches recipes" and "Sells items" sections without per-selection scans of the
    /// sources dictionaries. Called from every parse-and-swap whose inputs feed either
    /// index — both source files and both entity files (items / recipes), since the
    /// dictionary swap re-keys on InternalName.
    /// </summary>
    private void BuildNpcCrossLinkIndices()
    {
        var recipesTaught = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);
        foreach (var (recipeInternalName, sources) in _recipeSources)
        {
            if (!_recipesByInternalName.TryGetValue(recipeInternalName, out var recipe)) continue;
            foreach (var source in sources)
            {
                if (!string.Equals(source.Type, "Training", StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(source.Npc)) continue;
                if (!recipesTaught.TryGetValue(source.Npc, out var list))
                {
                    list = new List<Recipe>();
                    recipesTaught[source.Npc] = list;
                }
                if (!list.Contains(recipe))
                    list.Add(recipe);
            }
        }

        var itemsSold = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        foreach (var (itemInternalName, sources) in _itemSources)
        {
            if (!_itemsByInternalName.TryGetValue(itemInternalName, out var item)) continue;
            foreach (var source in sources)
            {
                if (!string.Equals(source.Type, "Vendor", StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(source.Npc)) continue;
                if (!itemsSold.TryGetValue(source.Npc, out var list))
                {
                    list = new List<Item>();
                    itemsSold[source.Npc] = list;
                }
                if (!list.Contains(item))
                    list.Add(item);
            }
        }

        var abilitiesTaught = new Dictionary<string, List<Ability>>(StringComparer.Ordinal);
        foreach (var (abilityInternalName, sources) in _abilitySources)
        {
            if (!_abilitiesByInternalName.TryGetValue(abilityInternalName, out var ability)) continue;
            foreach (var source in sources)
            {
                if (!string.Equals(source.Type, "Training", StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(source.Npc)) continue;
                if (!abilitiesTaught.TryGetValue(source.Npc, out var list))
                {
                    list = new List<Ability>();
                    abilitiesTaught[source.Npc] = list;
                }
                if (!list.Contains(ability))
                    list.Add(ability);
            }
        }

        _recipesTaughtByNpc = recipesTaught.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<Recipe>)kv.Value, StringComparer.Ordinal);
        _itemsSoldByNpc = itemsSold.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<Item>)kv.Value, StringComparer.Ordinal);
        _abilitiesTaughtByNpc = abilitiesTaught.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<Ability>)kv.Value, StringComparer.Ordinal);
    }

    private void ParseAndSwapSkills(IReadOnlyDictionary<string, PocoSkill> raw, ReferenceFileMetadata meta)
    {
        var byName = new Dictionary<string, SkillEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, v) in raw)
        {
            var entry = new SkillEntry(
                Key: key,
                DisplayName: v.Name ?? key,
                Id: v.Id,
                Combat: v.Combat,
                XpTable: v.XpTable ?? "",
                MaxBonusLevels: v.MaxBonusLevels,
                Parents: v.Parents?.ToArray() ?? [],
                Rewards: ProjectSkillRewards(v.Rewards));
            byName[key] = entry;
        }
        _skills = byName;
        _skillsSnapshot = new ReferenceFileSnapshot("skills", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byName.Count);
    }

    private static IReadOnlyDictionary<string, SkillRewardEntry> ProjectSkillRewards(
        IReadOnlyDictionary<string, Mithril.Reference.Models.Misc.SkillReward>? raw)
    {
        if (raw is null || raw.Count == 0)
            return new Dictionary<string, SkillRewardEntry>(StringComparer.Ordinal);
        var projected = new Dictionary<string, SkillRewardEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (level, reward) in raw)
        {
            projected[level] = new SkillRewardEntry(
                BonusToSkill: reward.BonusToSkill,
                Ability: reward.Ability?.ToArray() ?? [],
                Notes: reward.Notes,
                Recipe: reward.Recipe);
        }
        return projected;
    }

    private void ParseAndSwapXpTables(IReadOnlyDictionary<string, PocoXpTable> raw, ReferenceFileMetadata meta)
    {
        var byName = new Dictionary<string, XpTableEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (_, v) in raw)
        {
            if (string.IsNullOrEmpty(v.InternalName)) continue;
            var entry = new XpTableEntry(v.InternalName, v.XpAmounts ?? (IReadOnlyList<long>)[]);
            byName[v.InternalName] = entry;
        }
        _xpTables = byName;
        _xpTablesSnapshot = new ReferenceFileSnapshot("xptables", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byName.Count);
    }

    private void ParseAndSwapNpcs(IReadOnlyDictionary<string, PocoNpc> raw, ReferenceFileMetadata meta)
    {
        var byKey = new Dictionary<string, NpcEntry>(raw.Count, StringComparer.Ordinal);
        // The raw dictionary is already keyed by the NPC envelope key (== InternalName).
        // Copy it through with the Ordinal comparer to match the rest of the service.
        var byInternalName = new Dictionary<string, PocoNpc>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, v) in raw)
        {
            var prefs = (v.Preferences ?? (IReadOnlyList<PocoNpcPreference>)[])
                .Where(p => p.Keywords is { Count: > 0 })
                .Select(p => new NpcPreference(
                    p.Desire ?? "",
                    p.Keywords ?? (IReadOnlyList<string>)[],
                    p.Name ?? string.Join(", ", p.Keywords ?? (IReadOnlyList<string>)[]),
                    p.Pref,
                    p.Favor))
                .ToList();

            var services = (v.Services ?? (IReadOnlyList<PocoNpcService>)[])
                .Where(s => !string.IsNullOrEmpty(s.Type))
                .Select(s => new NpcService(
                    s.Type,
                    s.Favor,
                    s is PocoNpcStoreService store ? ParseCapIncreases(store.CapIncreases) : (IReadOnlyList<NpcStoreCapIncrease>)[]))
                .ToList();

            var entry = new NpcEntry(
                key,
                v.Name ?? key.Replace("NPC_", ""),
                v.AreaFriendlyName ?? "",
                prefs,
                v.ItemGifts ?? (IReadOnlyList<string>)[],
                services);

            byKey[key] = entry;
            byInternalName[key] = v;
        }
        _npcs = byKey;
        _npcsByInternalName = byInternalName;
        _npcsSnapshot = new ReferenceFileSnapshot("npcs", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byKey.Count);
        BuildQuestCrossLinkIndices();
        BuildAreaNpcCrossLinkIndex();
    }

    private void ParseAndSwapAreas(IReadOnlyDictionary<string, PocoArea> raw, ReferenceFileMetadata meta)
    {
        var byKey = new Dictionary<string, AreaEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, v) in raw)
        {
            var friendly = v.FriendlyName ?? key;
            var shortFriendly = string.IsNullOrEmpty(v.ShortFriendlyName) ? friendly : v.ShortFriendlyName;
            byKey[key] = new AreaEntry(key, friendly, shortFriendly);
        }
        _areas = byKey;
        _areasSnapshot = new ReferenceFileSnapshot("areas", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byKey.Count);
        BuildAreaNpcCrossLinkIndex();
    }

    private void ParseAndSwapLandmarks(IReadOnlyDictionary<string, IReadOnlyList<PocoLandmark>> raw, ReferenceFileMetadata meta)
    {
        // Defensive copy of the nested dictionary so the service owns the storage rather
        // than retaining a reference to the deserializer's allocation. Per-area lists are
        // already IReadOnlyList<Landmark>, which is the surface we expose; defer the inner
        // copy unless a consumer reports tearing on background refresh (none yet).
        var byArea = new Dictionary<string, IReadOnlyList<PocoLandmark>>(raw.Count, StringComparer.Ordinal);
        var landmarkCount = 0;
        foreach (var (key, list) in raw)
        {
            byArea[key] = list;
            landmarkCount += list.Count;
        }
        _landmarks = byArea;
        _landmarksSnapshot = new ReferenceFileSnapshot("landmarks", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, landmarkCount);
    }

    /// <summary>
    /// Builds <see cref="_npcsByArea"/> from <see cref="_npcsByInternalName"/>: groups NPCs
    /// whose <see cref="PocoNpc.AreaName"/> is set, keyed by the area key. Area keys without
    /// any NPCs are absent. Called from <see cref="ParseAndSwapNpcs"/> (NPC list changed)
    /// and <see cref="ParseAndSwapAreas"/> (area-key set changed — defensive even though the
    /// index doesn't gate on a known-area set today). Mirror of <see cref="BuildNpcCrossLinkIndices"/>'s
    /// reverse-lookup pattern.
    /// </summary>
    private void BuildAreaNpcCrossLinkIndex()
    {
        var byArea = new Dictionary<string, List<NpcEntry>>(StringComparer.Ordinal);
        foreach (var (internalName, poco) in _npcsByInternalName)
        {
            if (string.IsNullOrEmpty(poco.AreaName)) continue;
            if (!_npcs.TryGetValue(internalName, out var entry)) continue;
            if (!byArea.TryGetValue(poco.AreaName, out var list))
            {
                list = new List<NpcEntry>();
                byArea[poco.AreaName] = list;
            }
            list.Add(entry);
        }
        _npcsByArea = byArea.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<NpcEntry>)kv.Value, StringComparer.Ordinal);
    }

    private void ParseAndSwapLorebooks(IReadOnlyDictionary<string, Lorebook> raw, ReferenceFileMetadata meta)
    {
        var byKey = new Dictionary<string, Lorebook>(raw.Count, StringComparer.Ordinal);
        var byInternalName = new Dictionary<string, Lorebook>(raw.Count, StringComparer.Ordinal);
        var byId = new Dictionary<int, Lorebook>(raw.Count);
        foreach (var (key, v) in raw)
        {
            byKey[key] = v;
            if (!string.IsNullOrEmpty(v.InternalName))
                byInternalName[v.InternalName!] = v;
            // Envelope key is "Book_<N>"; lift the numeric suffix so Item.BestowLoreBook
            // (an int? matching <N>) can reverse-resolve to the book. Skip keys that don't
            // fit the convention rather than throwing — defensive against drift.
            if (TryLiftLorebookId(key, out var id))
                byId[id] = v;
        }
        _lorebooks = byKey;
        _lorebooksByInternalName = byInternalName;
        _lorebooksById = byId;
        _lorebooksSnapshot = new ReferenceFileSnapshot("lorebooks", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byKey.Count);
        BuildLorebookItemCrossLinkIndex();
    }

    private void ParseAndSwapLorebookInfo(LorebookInfo raw, ReferenceFileMetadata meta)
    {
        var byCategory = raw.Categories is null
            ? new Dictionary<string, LorebookCategoryInfo>(StringComparer.Ordinal)
            : new Dictionary<string, LorebookCategoryInfo>(raw.Categories, StringComparer.Ordinal);
        _lorebookCategories = byCategory;
        _lorebookInfoSnapshot = new ReferenceFileSnapshot("lorebookinfo", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byCategory.Count);
    }

    /// <summary>
    /// Lifts the numeric id from a <c>"Book_&lt;N&gt;"</c> envelope key. Returns false for
    /// keys that don't match the convention (defensive against a future PG key-shape change).
    /// </summary>
    private static bool TryLiftLorebookId(string envelopeKey, out int id)
    {
        id = 0;
        var underscore = envelopeKey.LastIndexOf('_');
        if (underscore < 0 || underscore == envelopeKey.Length - 1) return false;
        return int.TryParse(
            envelopeKey.AsSpan(underscore + 1),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out id);
    }

    /// <summary>
    /// Builds <see cref="_itemsBestowingLorebook"/>: lorebook InternalName → items whose
    /// <see cref="Item.BestowLoreBook"/> equals the book's numeric id. The single
    /// materialization for the #318 popup-from-index "Items that bestow this book" surface
    /// (single-reason — an item qualifies exactly one way). Called from
    /// <see cref="ParseAndSwapLorebooks"/> (book set / id mapping changed) and
    /// <see cref="ParseAndSwapItems"/> (the BestowLoreBook side changed) so a refresh of
    /// either file rebuilds the index against fresh POCO instances. The per-book list is
    /// dedup'd by item InternalName so the popup count equals distinct membership.
    /// </summary>
    private void BuildLorebookItemCrossLinkIndex()
    {
        if (_lorebooksById.Count == 0 || _itemsByInternalName.Count == 0)
        {
            _itemsBestowingLorebook = new Dictionary<string, IReadOnlyList<Item>>(StringComparer.Ordinal);
            return;
        }

        var acc = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var item in _itemsByInternalName.Values)
        {
            if (item.BestowLoreBook is not { } bookId) continue;
            if (!_lorebooksById.TryGetValue(bookId, out var book)) continue;
            if (string.IsNullOrEmpty(book.InternalName)) continue;
            var bookName = book.InternalName!;

            if (!acc.TryGetValue(bookName, out var list))
            {
                list = new List<Item>();
                acc[bookName] = list;
                seen[bookName] = new HashSet<string>(StringComparer.Ordinal);
            }
            var itemKey = item.InternalName ?? string.Empty;
            if (!seen[bookName].Add(itemKey)) continue;
            list.Add(item);
        }

        _itemsBestowingLorebook = acc.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<Item>)kv.Value
                .OrderBy(i => i.Name ?? i.InternalName ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList(),
            StringComparer.Ordinal);
    }

    private void ParseAndSwapDirectedGoals(IReadOnlyList<DirectedGoal> raw, ReferenceFileMetadata meta)
    {
        // Defensive copy with the same backing-store semantics as the rest of the
        // service: hand consumers a frozen IReadOnlyList wrapper over a snapshot list.
        _directedGoals = raw.ToArray();
        _directedGoalsSnapshot = new ReferenceFileSnapshot(
            "directedgoals", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, _directedGoals.Count);
    }

    private void ParseAndSwapAbilityKeywords(IReadOnlyList<AbilityKeyword> raw, ReferenceFileMetadata meta)
    {
        _abilityKeywordRules = raw.ToArray();
        _abilityKeywordRulesSnapshot = new ReferenceFileSnapshot(
            "abilitykeywords", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, _abilityKeywordRules.Count);
    }

    private void ParseAndSwapAbilityDynamicDots(IReadOnlyList<AbilityDynamicDot> raw, ReferenceFileMetadata meta)
    {
        _abilityDynamicDots = raw.ToArray();
        _abilityDynamicDotsSnapshot = new ReferenceFileSnapshot(
            "abilitydynamicdots", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, _abilityDynamicDots.Count);
    }

    private void ParseAndSwapAbilityDynamicSpecialValues(IReadOnlyList<AbilityDynamicSpecialValue> raw, ReferenceFileMetadata meta)
    {
        _abilityDynamicSpecialValues = raw.ToArray();
        _abilityDynamicSpecialValuesSnapshot = new ReferenceFileSnapshot(
            "abilitydynamicspecialvalues", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, _abilityDynamicSpecialValues.Count);
    }

    private void ParseAndSwapStrings(IReadOnlyDictionary<string, string> raw, ReferenceFileMetadata meta)
    {
        // Parsed dictionary is already string→string; just take a defensive
        // copy with Ordinal comparer to match the rest of the service's
        // dictionary conventions and freeze it as IReadOnlyDictionary.
        _strings = new Dictionary<string, string>(raw, StringComparer.Ordinal);
        _stringsSnapshot = new ReferenceFileSnapshot("strings_all", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, _strings.Count);
        // Rebuild cross-link indices: _keywordDisplayNames consults _strings, so a refresh of
        // strings_all changes the answer. The other two indices are unaffected but rebuilding
        // them is cheap (single recipe walk).
        BuildRecipeCrossLinkIndices();
    }

    /// <summary>Parses <c>"Despised:5000:Armor,Weapon,CorpseTrophy"</c> strings.</summary>
    private static IReadOnlyList<NpcStoreCapIncrease> ParseCapIncreases(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0) return [];
        var result = new List<NpcStoreCapIncrease>(raw.Count);
        foreach (var line in raw)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(':', 3);
            if (parts.Length < 2) continue;
            if (!int.TryParse(parts[1], out var cap)) continue;
            var keywords = parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2])
                ? (IReadOnlyList<string>)parts[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : [];
            result.Add(new NpcStoreCapIncrease(parts[0], cap, keywords));
        }
        return result;
    }

    private void ParseAndSwapItemSources(IReadOnlyDictionary<string, PocoSourceEnvelope> raw, ReferenceFileMetadata meta)
    {
        // sources_items.json shape: { "item_N": { "entries": [ { type, npc, ... }, ... ] } }
        var byInternalName = new Dictionary<string, IReadOnlyList<ItemSource>>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, envelope) in raw)
        {
            var underscore = key.IndexOf('_');
            if (underscore < 0) continue;
            if (!long.TryParse(key.AsSpan(underscore + 1), out var id)) continue;
            if (!_items.TryGetValue(id, out var item) || string.IsNullOrEmpty(item.InternalName)) continue;
            if (envelope.entries is null || envelope.entries.Count == 0) continue;

            var projected = new List<ItemSource>(envelope.entries.Count);
            foreach (var r in envelope.entries)
            {
                if (string.IsNullOrEmpty(r.type)) continue;
                projected.Add(new ItemSource(r.type, ExtractNpc(r), ResolveSourceContext(r)));
            }
            if (projected.Count > 0)
                byInternalName[item.InternalName!] = projected;
        }
        _itemSources = byInternalName;
        _itemSourcesSnapshot = new ReferenceFileSnapshot("sources_items", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byInternalName.Count);
        BuildNpcCrossLinkIndices();
    }

    private void ParseAndSwapRecipeSources(IReadOnlyDictionary<string, PocoSourceEnvelope> raw, ReferenceFileMetadata meta)
    {
        // sources_recipes.json shape: { "recipe_N": { "entries": [ { type, npc, ... }, ... ] } }
        // Mirrors ParseAndSwapItemSources; differs only in envelope-key prefix and the
        // dictionary it resolves against. ResolveSourceContext is shared between both
        // because Recipe / Quest sources resolve the same way regardless of which
        // sources_*.json file they were found in.
        var byInternalName = new Dictionary<string, IReadOnlyList<RecipeSource>>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, envelope) in raw)
        {
            if (!_recipes.TryGetValue(key, out var recipe) || string.IsNullOrEmpty(recipe.InternalName)) continue;
            if (envelope.entries is null || envelope.entries.Count == 0) continue;

            var projected = new List<RecipeSource>(envelope.entries.Count);
            foreach (var r in envelope.entries)
            {
                if (string.IsNullOrEmpty(r.type)) continue;
                projected.Add(new RecipeSource(r.type, ExtractNpc(r), ResolveSourceContext(r)));
            }
            if (projected.Count > 0)
                byInternalName[recipe.InternalName!] = projected;
        }
        _recipeSources = byInternalName;
        _recipeSourcesSnapshot = new ReferenceFileSnapshot("sources_recipes", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byInternalName.Count);
        BuildNpcCrossLinkIndices();
    }

    private void ParseAndSwapAbilities(IReadOnlyDictionary<string, Ability> raw, ReferenceFileMetadata meta)
    {
        var byKey = new Dictionary<string, Ability>(raw.Count, StringComparer.Ordinal);
        var byInternalName = new Dictionary<string, Ability>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, ability) in raw)
        {
            if (ability is null) continue;
            byKey[key] = ability;
            if (!string.IsNullOrEmpty(ability.InternalName))
                byInternalName[ability.InternalName!] = ability;
        }
        _abilities = byKey;
        _abilitiesByInternalName = byInternalName;
        _abilitiesSnapshot = new ReferenceFileSnapshot("abilities", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byKey.Count);
        BuildAbilityCrossLinkIndices();
        // Re-derive AbilitiesTaughtByNpc when the ability set itself changes.
        BuildNpcCrossLinkIndices();
        // Re-derive AbilitiesByEffectKeyword — keyed on Ability.EffectKeywordReqs ∪
        // EffectKeywordsIndicatingEnabled ∪ TargetEffectKeywordReq, so any abilities-file
        // refresh changes the membership of those sets.
        BuildEffectAbilityCrossLinkIndices();
    }

    private void ParseAndSwapEffects(IReadOnlyDictionary<string, Effect> raw, ReferenceFileMetadata meta)
    {
        var byKey = new Dictionary<string, Effect>(raw.Count, StringComparer.Ordinal);
        var byInternalName = new Dictionary<string, Effect>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, effect) in raw)
        {
            if (effect is null) continue;
            byKey[key] = effect;
            // ParseEffects lifts the envelope key onto effect.InternalName; the InternalName
            // and envelope-key dictionaries therefore carry the same entries with the same
            // keys, but the sibling lookup mirrors the shape of ItemsByInternalName /
            // AbilitiesByInternalName so resolver and kind-target code paths don't special-case.
            if (!string.IsNullOrEmpty(effect.InternalName))
                byInternalName[effect.InternalName!] = effect;
        }
        _effects = byKey;
        _effectsByInternalName = byInternalName;
        _effectsSnapshot = new ReferenceFileSnapshot("effects", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byKey.Count);
        BuildEffectAbilityCrossLinkIndices();
    }

    /// <summary>
    /// Builds the four effect/ability cross-link indices: <see cref="_effectsByKeyword"/>,
    /// <see cref="_effectsByStackingType"/>, <see cref="_effectsByTriggeringAbilityKeyword"/>,
    /// and <see cref="_abilitiesByEffectKeyword"/>. The last index reads ability fields and
    /// must rebuild when either <c>effects.json</c> or <c>abilities.json</c> reloads — both
    /// <see cref="ParseAndSwapEffects"/> and <see cref="ParseAndSwapAbilities"/> call this.
    /// Abilities with <see cref="Ability.InternalAbility"/> = <c>true</c> are excluded from
    /// <see cref="_abilitiesByEffectKeyword"/> per the on-detail chip-cluster contract.
    /// <para>
    /// <see cref="_abilitiesByEffectKeyword"/> retains <i>why</i> each ability qualified:
    /// its members are <see cref="EffectAbilityMatch"/> records carrying
    /// <see cref="EffectAbilityMatchReason"/> flags for the matching field(s). A
    /// multi-field ability is carried once with its reason flags OR'd, so the index is
    /// still dedup'd by ability (a distinct-member count equals the displayed
    /// "View all N").
    /// </para>
    /// </summary>
    private void BuildEffectAbilityCrossLinkIndices()
    {
        var byKeyword = new Dictionary<string, List<Effect>>(StringComparer.Ordinal);
        var byStacking = new Dictionary<string, List<Effect>>(StringComparer.Ordinal);
        var byTriggeringAbilityKeyword = new Dictionary<string, List<Effect>>(StringComparer.Ordinal);

        foreach (var effect in _effects.Values)
        {
            if (effect.Keywords is { Count: > 0 } keywords)
            {
                foreach (var tag in keywords)
                {
                    if (string.IsNullOrEmpty(tag)) continue;
                    if (!byKeyword.TryGetValue(tag, out var list))
                    {
                        list = new List<Effect>();
                        byKeyword[tag] = list;
                    }
                    list.Add(effect);
                }
            }

            if (!string.IsNullOrEmpty(effect.StackingType))
            {
                if (!byStacking.TryGetValue(effect.StackingType!, out var list))
                {
                    list = new List<Effect>();
                    byStacking[effect.StackingType!] = list;
                }
                list.Add(effect);
            }

            if (effect.AbilityKeywords is { Count: > 0 } abilityKeywords)
            {
                foreach (var tag in abilityKeywords)
                {
                    if (string.IsNullOrEmpty(tag)) continue;
                    if (!byTriggeringAbilityKeyword.TryGetValue(tag, out var list))
                    {
                        list = new List<Effect>();
                        byTriggeringAbilityKeyword[tag] = list;
                    }
                    list.Add(effect);
                }
            }
        }

        // tag → ability internal name → accumulated match reason flags. The reason is
        // accumulated (OR'd) per (tag, ability) so an ability qualifying via several of
        // the three unioned fields is carried ONCE with multiple reason flags set —
        // dedup intent preserved (a distinct-member count equals the displayed
        // "View all N"), provenance complete (the popup can section by reason).
        var byEffectKeyword =
            new Dictionary<string, Dictionary<string, (Ability Ability, EffectAbilityMatchReason Reason)>>(
                StringComparer.Ordinal);

        foreach (var ability in _abilities.Values)
        {
            if (ability.InternalAbility == true) continue;
            if (string.IsNullOrEmpty(ability.InternalName)) continue;

            // Union: EffectKeywordReqs ∪ EffectKeywordsIndicatingEnabled ∪ {TargetEffectKeywordReq}.
            // Unlike the old flatten-to-HashSet<Ability>, the matching field is retained as a
            // reason flag — this is the provenance the index used to discard.
            void Mark(string? tag, EffectAbilityMatchReason reason)
            {
                if (string.IsNullOrEmpty(tag)) return;
                if (!byEffectKeyword.TryGetValue(tag!, out var perAbility))
                {
                    perAbility = new Dictionary<string, (Ability, EffectAbilityMatchReason)>(StringComparer.Ordinal);
                    byEffectKeyword[tag!] = perAbility;
                }
                if (perAbility.TryGetValue(ability.InternalName!, out var existing))
                    perAbility[ability.InternalName!] = (existing.Ability, existing.Reason | reason);
                else
                    perAbility[ability.InternalName!] = (ability, reason);
            }

            if (ability.EffectKeywordReqs is { Count: > 0 } reqs)
                foreach (var tag in reqs) Mark(tag, EffectAbilityMatchReason.Requires);
            if (ability.EffectKeywordsIndicatingEnabled is { Count: > 0 } enabled)
                foreach (var tag in enabled) Mark(tag, EffectAbilityMatchReason.EnabledBy);
            Mark(ability.TargetEffectKeywordReq, EffectAbilityMatchReason.Targets);
        }

        _effectsByKeyword = byKeyword.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<Effect>)kv.Value, StringComparer.Ordinal);
        _effectsByStackingType = byStacking.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<Effect>)kv.Value, StringComparer.Ordinal);
        _effectsByTriggeringAbilityKeyword = byTriggeringAbilityKeyword.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<Effect>)kv.Value, StringComparer.Ordinal);
        _abilitiesByEffectKeyword = byEffectKeyword.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<EffectAbilityMatch>)kv.Value.Values
                .Select(v => new EffectAbilityMatch(v.Ability, v.Reason))
                .ToList(),
            StringComparer.Ordinal);
    }

    private void ParseAndSwapAbilitySources(IReadOnlyDictionary<string, PocoSourceEnvelope> raw, ReferenceFileMetadata meta)
    {
        // sources_abilities.json shape mirrors sources_items / sources_recipes:
        // { "ability_N": { "entries": [ { type, npc, ... }, ... ] } }.
        var byInternalName = new Dictionary<string, IReadOnlyList<AbilitySource>>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, envelope) in raw)
        {
            if (!_abilities.TryGetValue(key, out var ability) || string.IsNullOrEmpty(ability.InternalName)) continue;
            if (envelope.entries is null || envelope.entries.Count == 0) continue;

            var projected = new List<AbilitySource>(envelope.entries.Count);
            foreach (var r in envelope.entries)
            {
                if (string.IsNullOrEmpty(r.type)) continue;
                projected.Add(new AbilitySource(r.type, ExtractNpc(r), ResolveSourceContext(r)));
            }
            if (projected.Count > 0)
                byInternalName[ability.InternalName!] = projected;
        }
        _abilitySources = byInternalName;
        _abilitySourcesSnapshot = new ReferenceFileSnapshot("sources_abilities", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byInternalName.Count);
        BuildNpcCrossLinkIndices();
    }

    /// <summary>
    /// Build the derived ability indices (<see cref="_abilitiesBySkill"/>,
    /// <see cref="_abilitiesUpgradingFrom"/>, <see cref="_abilitiesInGroup"/>) from the current
    /// <see cref="_abilities"/> map. AbilitiesTaughtByNpc derives from <see cref="_abilitySources"/>
    /// and lives in <see cref="BuildNpcCrossLinkIndices"/> with the recipe / item teaching indices.
    /// </summary>
    private void BuildAbilityCrossLinkIndices()
    {
        var bySkill = new Dictionary<string, List<Ability>>(StringComparer.Ordinal);
        var upgradingFrom = new Dictionary<string, List<Ability>>(StringComparer.Ordinal);
        var inGroup = new Dictionary<string, List<Ability>>(StringComparer.Ordinal);

        foreach (var ability in _abilities.Values)
        {
            if (!string.IsNullOrEmpty(ability.Skill))
            {
                if (!bySkill.TryGetValue(ability.Skill!, out var list))
                {
                    list = new List<Ability>();
                    bySkill[ability.Skill!] = list;
                }
                list.Add(ability);
            }

            if (!string.IsNullOrEmpty(ability.UpgradeOf))
            {
                if (!upgradingFrom.TryGetValue(ability.UpgradeOf!, out var list))
                {
                    list = new List<Ability>();
                    upgradingFrom[ability.UpgradeOf!] = list;
                }
                list.Add(ability);
            }

            if (!string.IsNullOrEmpty(ability.AbilityGroup))
            {
                if (!inGroup.TryGetValue(ability.AbilityGroup!, out var list))
                {
                    list = new List<Ability>();
                    inGroup[ability.AbilityGroup!] = list;
                }
                list.Add(ability);
            }
        }

        _abilitiesBySkill = bySkill.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<Ability>)kv.Value, StringComparer.Ordinal);
        _abilitiesUpgradingFrom = upgradingFrom.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<Ability>)kv.Value, StringComparer.Ordinal);
        _abilitiesInGroup = inGroup.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<Ability>)kv.Value, StringComparer.Ordinal);
    }

    private static string? ExtractNpc(SourceModels.SourceEntry s) => s switch
    {
        SourceModels.VendorSource v => v.npc,
        SourceModels.BarterSource b => b.npc,
        SourceModels.NpcGiftSource n => n.npc,
        SourceModels.HangOutSource h => h.npc,
        SourceModels.TrainingSource t => t.npc,
        _ => null,
    };

    /// <summary>
    /// Resolve a polymorphic <see cref="SourceModels.SourceEntry"/> to its
    /// <see cref="ItemSource.Context"/> string. Recipe / Quest sources carry a
    /// numeric id that we look up against the recipes / quests dictionaries
    /// to surface the InternalName. Relies on <see cref="LoadRecipes"/> and
    /// <see cref="LoadQuests"/> running before <see cref="LoadItemSources"/>.
    /// </summary>
    private string? ResolveSourceContext(SourceModels.SourceEntry s) => s switch
    {
        SourceModels.RecipeSource r when _recipes.TryGetValue($"recipe_{r.recipeId}", out var recipe) => recipe.InternalName,
        SourceModels.QuestSource q when _quests.TryGetValue($"quest_{q.questId}", out var quest) => quest.InternalName,
        SourceModels.QuestObjectiveMacGuffinSource qm when _quests.TryGetValue($"quest_{qm.questId}", out var quest) => quest.InternalName,
        SourceModels.CraftedInteractorSource ci => ci.friendlyName,
        SourceModels.ResourceInteractorSource ri => ri.friendlyName,
        SourceModels.SkillSource sk => sk.skill,
        _ => null,
    };

    private void ParseAndSwapAttributes(IReadOnlyDictionary<string, PocoAttribute> raw, ReferenceFileMetadata meta)
    {
        var byToken = new Dictionary<string, AttributeEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (token, v) in raw)
        {
            if (string.IsNullOrEmpty(token)) continue;
            var entry = new AttributeEntry(
                Token: token,
                Label: v.Label ?? token,
                DisplayType: v.DisplayType ?? "",
                DisplayRule: v.DisplayRule ?? "Always",
                DefaultValue: v.DefaultValue,
                IconIds: v.IconIds ?? (IReadOnlyList<int>)[]);
            byToken[token] = entry;
        }
        _attributes = byToken;
        _attributesSnapshot = new ReferenceFileSnapshot("attributes", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byToken.Count);
    }

    private void ParseAndSwapPowers(IReadOnlyDictionary<string, PocoPower> raw, ReferenceFileMetadata meta)
    {
        // Key the output by PowerEntry.InternalName so recipe effects
        // (AddItemTSysPower(<InternalName>, <tier>)) resolve directly.
        var byInternalName = new Dictionary<string, PowerEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (_, v) in raw)
        {
            if (string.IsNullOrEmpty(v.InternalName)) continue;

            var tiers = new Dictionary<int, PowerTier>();
            if (v.Tiers is not null)
            {
                foreach (var (tierKey, rawTier) in v.Tiers)
                {
                    // Keys are "id_N". Parse the numeric suffix; skip malformed entries.
                    var underscore = tierKey.IndexOf('_');
                    if (underscore < 0) continue;
                    if (!int.TryParse(tierKey.AsSpan(underscore + 1), out var tierNum)) continue;

                    var descs = rawTier.EffectDescs ?? (IReadOnlyList<string>)[];
                    tiers[tierNum] = new PowerTier(
                        tierNum,
                        descs,
                        rawTier.MaxLevel,
                        MinLevel: rawTier.MinLevel,
                        MinRarity: string.IsNullOrEmpty(rawTier.MinRarity) ? null : rawTier.MinRarity,
                        SkillLevelPrereq: rawTier.SkillLevelPrereq);
                }
            }

            var entry = new PowerEntry(
                InternalName: v.InternalName,
                Skill: v.Skill ?? "",
                Slots: v.Slots ?? (IReadOnlyList<string>)[],
                Suffix: string.IsNullOrEmpty(v.Suffix) ? null : v.Suffix,
                Tiers: tiers);
            byInternalName[entry.InternalName] = entry;
        }
        _powers = byInternalName;
        _powersSnapshot = new ReferenceFileSnapshot("tsysclientinfo", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byInternalName.Count);
    }

    private void ParseAndSwapProfiles(IReadOnlyDictionary<string, IReadOnlyList<string>> raw, ReferenceFileMetadata meta)
    {
        var byProfile = new Dictionary<string, IReadOnlyList<string>>(raw.Count, StringComparer.Ordinal);
        foreach (var (profileName, powers) in raw)
        {
            if (string.IsNullOrEmpty(profileName) || powers is null) continue;
            byProfile[profileName] = powers;
        }
        _profiles = byProfile;
        _profilesSnapshot = new ReferenceFileSnapshot("tsysprofiles", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byProfile.Count);
    }

    private void ParseAndSwapQuests(IReadOnlyDictionary<string, Quest> raw, ReferenceFileMetadata meta)
    {
        var byKey = new Dictionary<string, Quest>(raw.Count, StringComparer.Ordinal);
        var byName = new Dictionary<string, Quest>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, v) in raw)
        {
            byKey[key] = v;
            if (!string.IsNullOrEmpty(v.InternalName)) byName[v.InternalName] = v;
        }
        _quests = byKey;
        _questsByInternalName = byName;
        _questsSnapshot = new ReferenceFileSnapshot("quests", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byKey.Count);
        BuildQuestCrossLinkIndices();
    }

    /// <summary>
    /// Builds <see cref="_questsByGiverNpc"/> and <see cref="_questsRewardingItem"/> from the
    /// current <see cref="_quests"/> + <see cref="_npcsByInternalName"/> + <see cref="_itemsByInternalName"/>.
    /// Giver index merges <see cref="Quest.QuestNpc"/> and <see cref="Quest.FavorNpc"/> — both
    /// commonly reference the same NPC and the detail view wants a single "quests this NPC is
    /// involved in" list. Rewarding-item index walks <see cref="Quest.Rewards_Items"/> only;
    /// NamedLootProfile is opaque without further parsing. Called from every parse-and-swap whose
    /// inputs feed either index (quests / npcs / items).
    /// </summary>
    private void BuildQuestCrossLinkIndices()
    {
        var byGiver = new Dictionary<string, List<Quest>>(StringComparer.Ordinal);
        var byRewardItem = new Dictionary<string, List<Quest>>(StringComparer.Ordinal);

        foreach (var quest in _quests.Values)
        {
            AddGiverLink(byGiver, quest.QuestNpc, quest);
            AddGiverLink(byGiver, quest.FavorNpc, quest);

            if (quest.Rewards_Items is { Count: > 0 } rewards)
            {
                foreach (var reward in rewards)
                {
                    if (string.IsNullOrEmpty(reward.Item)) continue;
                    if (!_itemsByInternalName.ContainsKey(reward.Item)) continue;
                    if (!byRewardItem.TryGetValue(reward.Item, out var list))
                    {
                        list = new List<Quest>();
                        byRewardItem[reward.Item] = list;
                    }
                    if (!list.Contains(quest))
                        list.Add(quest);
                }
            }
        }

        _questsByGiverNpc = byGiver.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<Quest>)kv.Value, StringComparer.Ordinal);
        _questsRewardingItem = byRewardItem.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<Quest>)kv.Value, StringComparer.Ordinal);

        void AddGiverLink(Dictionary<string, List<Quest>> map, string? npcInternalName, Quest quest)
        {
            if (string.IsNullOrEmpty(npcInternalName)) return;
            // Drop refs to NPCs the catalog doesn't know about — they can't be
            // navigated to anyway and the chip would render as plain text.
            if (!_npcsByInternalName.ContainsKey(npcInternalName)) return;
            if (!map.TryGetValue(npcInternalName, out var list))
            {
                list = new List<Quest>();
                map[npcInternalName] = list;
            }
            if (!list.Contains(quest))
                list.Add(quest);
        }
    }

    // ── Shared helpers ───────────────────────────────────────────────────

    private ReferenceFileMetadata TryLoadMetadata(string path, ReferenceFileSource defaultSource)
    {
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var meta = JsonSerializer.Deserialize(stream, ReferenceJsonContext.Default.ReferenceFileMetadata);
                if (meta is not null)
                {
                    if (string.IsNullOrEmpty(meta.CdnVersion)) meta.CdnVersion = FallbackCdnVersion;
                    return meta;
                }
            }
            catch { }
        }
        return new ReferenceFileMetadata { CdnVersion = FallbackCdnVersion, Source = defaultSource };
    }
}
