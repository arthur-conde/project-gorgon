using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Mithril.Reference;
using Mithril.Reference.Serialization;
using Mithril.Shared.Diagnostics;
using PocoArea = Mithril.Reference.Models.Misc.Area;
using PocoAttribute = Mithril.Reference.Models.Misc.AttributeDef;
using PocoItem = Mithril.Reference.Models.Items.Item;
using PocoNpc = Mithril.Reference.Models.Npcs.Npc;
using PocoNpcPreference = Mithril.Reference.Models.Npcs.NpcPreference;
using PocoNpcService = Mithril.Reference.Models.Npcs.NpcService;
using PocoNpcStoreService = Mithril.Reference.Models.Npcs.StoreService;
using PocoPower = Mithril.Reference.Models.Misc.PowerProfile;
using PocoQuest = Mithril.Reference.Models.Quests.Quest;
using PocoQuestObjective = Mithril.Reference.Models.Quests.QuestObjective;
using PocoQuestRequirement = Mithril.Reference.Models.Quests.QuestRequirement;
using PocoRecipe = Mithril.Reference.Models.Recipes.Recipe;
using PocoRecipeIngredient = Mithril.Reference.Models.Recipes.RecipeIngredient;
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
    private IReadOnlyDictionary<long, ItemEntry> _items = new Dictionary<long, ItemEntry>();
    private IReadOnlyDictionary<string, ItemEntry> _itemsByInternalName =
        new Dictionary<string, ItemEntry>(StringComparer.Ordinal);
    private ItemKeywordIndex _keywordIndex = ItemKeywordIndex.Empty;
    private ReferenceFileSnapshot _itemsSnapshot;

    // Recipes
    private IReadOnlyDictionary<string, RecipeEntry> _recipes = new Dictionary<string, RecipeEntry>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, RecipeEntry> _recipesByInternalName =
        new Dictionary<string, RecipeEntry>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _recipesSnapshot;

    // Skills
    private IReadOnlyDictionary<string, SkillEntry> _skills = new Dictionary<string, SkillEntry>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _skillsSnapshot;

    // XP Tables
    private IReadOnlyDictionary<string, XpTableEntry> _xpTables = new Dictionary<string, XpTableEntry>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _xpTablesSnapshot;

    // NPCs
    private IReadOnlyDictionary<string, NpcEntry> _npcs = new Dictionary<string, NpcEntry>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _npcsSnapshot;

    // Areas (areas.json) — area code → friendly display names.
    private IReadOnlyDictionary<string, AreaEntry> _areas = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _areasSnapshot;

    // Item sources (sources_items.json)
    private IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> _itemSources =
        new Dictionary<string, IReadOnlyList<ItemSource>>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _itemSourcesSnapshot;

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
    private IReadOnlyDictionary<string, QuestEntry> _quests = new Dictionary<string, QuestEntry>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, QuestEntry> _questsByInternalName =
        new Dictionary<string, QuestEntry>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _questsSnapshot;

    public ReferenceDataService(string cacheDir, HttpClient http, IDiagnosticsSink? diag = null, string? bundledDir = null)
    {
        _cacheDir = cacheDir;
        _http = http;
        _diag = diag;
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
        _itemSourcesSnapshot = new ReferenceFileSnapshot("sources_items", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _attributesSnapshot = new ReferenceFileSnapshot("attributes", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _powersSnapshot = new ReferenceFileSnapshot("tsysclientinfo", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _profilesSnapshot = new ReferenceFileSnapshot("tsysprofiles", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _questsSnapshot = new ReferenceFileSnapshot("quests", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);

        LoadItems();
        LoadRecipes();
        LoadSkills();
        LoadXpTables();
        LoadNpcs();
        LoadAreas();
        LoadQuests();              // Must run before LoadItemSources — ResolveSourceContext reads _quests.
        LoadItemSources();
        LoadAttributes();
        LoadPowers();
        LoadProfiles();
    }

    public IReadOnlyList<string> Keys { get; } = ["items", "recipes", "skills", "xptables", "npcs", "areas", "sources_items", "attributes", "tsysclientinfo", "tsysprofiles", "quests"];

    public IReadOnlyDictionary<long, ItemEntry> Items => _items;
    public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName => _itemsByInternalName;
    public ItemKeywordIndex KeywordIndex => _keywordIndex;
    public IReadOnlyDictionary<string, RecipeEntry> Recipes => _recipes;
    public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName => _recipesByInternalName;
    public IReadOnlyDictionary<string, SkillEntry> Skills => _skills;
    public IReadOnlyDictionary<string, XpTableEntry> XpTables => _xpTables;
    public IReadOnlyDictionary<string, NpcEntry> Npcs => _npcs;
    public IReadOnlyDictionary<string, AreaEntry> Areas => _areas;
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources => _itemSources;
    public IReadOnlyDictionary<string, AttributeEntry> Attributes => _attributes;
    public IReadOnlyDictionary<string, PowerEntry> Powers => _powers;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles => _profiles;
    public IReadOnlyDictionary<string, QuestEntry> Quests => _quests;
    public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName => _questsByInternalName;

    public ReferenceFileSnapshot GetSnapshot(string key) => key switch
    {
        "items" => _itemsSnapshot,
        "recipes" => _recipesSnapshot,
        "skills" => _skillsSnapshot,
        "xptables" => _xpTablesSnapshot,
        "npcs" => _npcsSnapshot,
        "areas" => _areasSnapshot,
        "sources_items" => _itemSourcesSnapshot,
        "attributes" => _attributesSnapshot,
        "tsysclientinfo" => _powersSnapshot,
        "tsysprofiles" => _profilesSnapshot,
        "quests" => _questsSnapshot,
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
        "sources_items" => RefreshFileAsync("sources_items", ReferenceDeserializer.ParseSources, ParseAndSwapItemSources, ct),
        "attributes" => RefreshFileAsync("attributes", ReferenceDeserializer.ParseAttributes, ParseAndSwapAttributes, ct),
        "tsysclientinfo" => RefreshFileAsync("tsysclientinfo", ReferenceDeserializer.ParseTsysClientInfo, ParseAndSwapPowers, ct),
        "tsysprofiles" => RefreshFileAsync("tsysprofiles", ReferenceDeserializer.ParseTsysProfiles, ParseAndSwapProfiles, ct),
        "quests" => RefreshFileAsync("quests", ReferenceDeserializer.ParseQuests, ParseAndSwapQuests, ct),
        _ => throw new ArgumentException($"Unknown reference file key: {key}", nameof(key)),
    };

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await RefreshAsync("items", ct);
        await RefreshAsync("recipes", ct);
        await RefreshAsync("skills", ct);
        await RefreshAsync("xptables", ct);
        await RefreshAsync("npcs", ct);
        await RefreshAsync("areas", ct);
        await RefreshAsync("sources_items", ct);
        await RefreshAsync("attributes", ct);
        await RefreshAsync("tsysclientinfo", ct);
        await RefreshAsync("tsysprofiles", ct);
        await RefreshAsync("quests", ct);
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
        var version = await CdnVersionDetector.TryDetectAsync(_http, CdnRoot, ct)
                      ?? GetSnapshot(fileName).CdnVersion
                      ?? FallbackCdnVersion;
        var url = $"{CdnRoot}{version}/data/{fileName}.json";
        _diag?.Info("Reference", $"Refreshing {fileName} from {url}.");

        byte[] body;
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            body = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Reference", $"{fileName}.json fetch failed ({ex.Message}); keeping existing data.");
            return;
        }

        var meta = new ReferenceFileMetadata
        {
            CdnVersion = version,
            FetchedAtUtc = DateTimeOffset.UtcNow,
            Source = ReferenceFileSource.Cdn,
        };

        Directory.CreateDirectory(_cacheDir);
        var cachePath = Path.Combine(_cacheDir, $"{fileName}.json");
        var metaPath = Path.Combine(_cacheDir, $"{fileName}.meta.json");
        await Settings.AtomicFile.WriteAllBytesAtomicAsync(cachePath, body, ct);
        await Settings.AtomicFile.WriteJsonAtomicAsync(metaPath, meta,
            ReferenceJsonContext.Default.ReferenceFileMetadata, ct);

        var json = Encoding.UTF8.GetString(body);
        var parsed = parser(json);
        swapper(parsed, meta);
        ReportUnknowns(fileName, parsed!, meta.CdnVersion);
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
    private void LoadItemSources() => LoadFile("sources_items", ReferenceDeserializer.ParseSources, ParseAndSwapItemSources);
    private void LoadAttributes() => LoadFile("attributes", ReferenceDeserializer.ParseAttributes, ParseAndSwapAttributes);
    private void LoadPowers() => LoadFile("tsysclientinfo", ReferenceDeserializer.ParseTsysClientInfo, ParseAndSwapPowers);
    private void LoadProfiles() => LoadFile("tsysprofiles", ReferenceDeserializer.ParseTsysProfiles, ParseAndSwapProfiles);
    private void LoadQuests() => LoadFile("quests", ReferenceDeserializer.ParseQuests, ParseAndSwapQuests);

    // ── Per-type parse-and-swap ──────────────────────────────────────────

    private void ParseAndSwapItems(IReadOnlyDictionary<string, PocoItem> raw, ReferenceFileMetadata meta)
    {
        var byId = new Dictionary<long, ItemEntry>(raw.Count);
        var byName = new Dictionary<string, ItemEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, v) in raw)
        {
            var underscore = key.IndexOf('_');
            if (underscore < 0) continue;
            if (!long.TryParse(key.AsSpan(underscore + 1), out var id)) continue;
            var skillPrereqs = v.SkillReqs?.Keys.ToList();
            var keywords = ParseKeywords(v.Keywords, v.EquipSlot, skillPrereqs, v.Value);
            var entry = new ItemEntry(
                id,
                v.Name ?? "",
                v.InternalName ?? "",
                v.MaxStackSize,
                v.IconId,
                keywords,
                v.EquipSlot,
                skillPrereqs,
                (decimal)v.Value,
                v.FoodDesc,
                v.SkillReqs,
                v.EffectDescs,
                v.Description,
                string.IsNullOrEmpty(v.TSysProfile) ? null : v.TSysProfile,
                v.CraftingTargetLevel);
            byId[id] = entry;
            if (!string.IsNullOrEmpty(entry.InternalName)) byName[entry.InternalName] = entry;
        }
        _items = byId;
        _itemsByInternalName = byName;
        _keywordIndex = new ItemKeywordIndex(byId);
        _itemsSnapshot = new ReferenceFileSnapshot("items", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byId.Count);
    }

    /// <summary>
    /// Parses raw keyword strings and synthesizes virtual keywords from item metadata.
    /// NPC preferences use filter keywords like "SkillPrereq:Archery", "EquipmentSlot:Head",
    /// "MinRarity:Rare", "MinValue:1000" that don't appear in item keyword arrays directly.
    /// We synthesize these as additional ItemKeyword entries so the GiftIndex can match them.
    /// </summary>
    private static IReadOnlyList<ItemKeyword> ParseKeywords(
        IReadOnlyList<string>? raw, string? equipSlot, List<string>? skillPrereqs, double value)
    {
        var result = new List<ItemKeyword>((raw?.Count ?? 0) + 4);

        if (raw is not null)
        {
            foreach (var s in raw)
            {
                var eq = s.IndexOf('=');
                if (eq > 0 && int.TryParse(s.AsSpan(eq + 1), out var quality))
                    result.Add(new ItemKeyword(s[..eq], quality));
                else
                    result.Add(new ItemKeyword(s, 0));
            }
        }

        // Synthesize "EquipmentSlot:{slot}" virtual keyword
        if (!string.IsNullOrEmpty(equipSlot))
            result.Add(new ItemKeyword($"EquipmentSlot:{equipSlot}", 0));

        // Synthesize "SkillPrereq:{skill}" virtual keywords
        if (skillPrereqs is not null)
        {
            foreach (var skill in skillPrereqs)
                result.Add(new ItemKeyword($"SkillPrereq:{skill}", 0));
        }

        // Synthesize "MinValue:{threshold}" virtual keywords for common thresholds
        var v = (int)value;
        if (v >= 1000) result.Add(new ItemKeyword("MinValue:1000", 0));
        if (v >= 500) result.Add(new ItemKeyword("MinValue:500", 0));

        // Synthesize rarity virtual keywords from the keyword list.
        // "Loot" items can drop at any rarity; "Stock" items are always Common.
        var hasLoot = raw?.Contains("Loot") == true;
        var hasEquipment = raw?.Contains("Equipment") == true;
        if (hasLoot)
        {
            // Loot items can be any rarity — match all MinRarity filters
            result.Add(new ItemKeyword("MinRarity:Uncommon", 0));
            result.Add(new ItemKeyword("MinRarity:Rare", 0));
            result.Add(new ItemKeyword("MinRarity:Epic", 0));
        }
        else if (hasEquipment)
        {
            result.Add(new ItemKeyword("Rarity:Common", 0));
        }

        return result;
    }

    private void ParseAndSwapRecipes(IReadOnlyDictionary<string, PocoRecipe> raw, ReferenceFileMetadata meta)
    {
        var byKey = new Dictionary<string, RecipeEntry>(raw.Count, StringComparer.Ordinal);
        var byName = new Dictionary<string, RecipeEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, v) in raw)
        {
            var ingredients = v.Ingredients?
                .Select(ProjectIngredient)
                .Where(x => x is not null)
                .Select(x => x!)
                .ToList()
                ?? (IReadOnlyList<RecipeIngredient>)[];

            var results = v.ResultItems?
                .Where(i => i.ItemCode != 0)
                .Select(i => new RecipeItemRef(i.ItemCode, i.StackSize, null))
                .ToList()
                ?? (IReadOnlyList<RecipeItemRef>)[];

            var protoResults = v.ProtoResultItems?
                .Where(i => i.ItemCode != 0)
                .Select(i => new RecipeItemRef(i.ItemCode, i.StackSize, null))
                .ToList()
                ?? (IReadOnlyList<RecipeItemRef>)[];

            var resultEffects = v.ResultEffects ?? (IReadOnlyList<string>)[];

            var entry = new RecipeEntry(
                key,
                v.Name ?? "",
                v.InternalName ?? "",
                v.IconId,
                v.Skill ?? "",
                v.SkillLevelReq,
                v.RewardSkill ?? "",
                v.RewardSkillXp,
                v.RewardSkillXpFirstTime,
                v.RewardSkillXpDropOffLevel,
                (float?)v.RewardSkillXpDropOffPct,
                v.RewardSkillXpDropOffRate,
                ingredients,
                results,
                v.PrereqRecipe,
                protoResults,
                resultEffects);
            byKey[key] = entry;
            if (!string.IsNullOrEmpty(entry.InternalName)) byName[entry.InternalName] = entry;
        }
        _recipes = byKey;
        _recipesByInternalName = byName;
        _recipesSnapshot = new ReferenceFileSnapshot("recipes", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byKey.Count);
    }

    private static RecipeIngredient? ProjectIngredient(PocoRecipeIngredient i)
    {
        if (i.ItemCode is { } itemCode)
            return new RecipeItemIngredient(itemCode, i.StackSize, (float?)i.ChanceToConsume);
        if (i.ItemKeys is { Count: > 0 } keys)
            return new RecipeKeywordIngredient(keys, i.Desc, i.StackSize, (float?)i.ChanceToConsume);
        return null;
    }

    private void ParseAndSwapSkills(IReadOnlyDictionary<string, PocoSkill> raw, ReferenceFileMetadata meta)
    {
        var byName = new Dictionary<string, SkillEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, v) in raw)
        {
            var entry = new SkillEntry(key, v.Id, v.Combat, v.XpTable ?? "", v.MaxBonusLevels);
            byName[key] = entry;
        }
        _skills = byName;
        _skillsSnapshot = new ReferenceFileSnapshot("skills", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byName.Count);
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
        }
        _npcs = byKey;
        _npcsSnapshot = new ReferenceFileSnapshot("npcs", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byKey.Count);
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
                byInternalName[item.InternalName] = projected;
        }
        _itemSources = byInternalName;
        _itemSourcesSnapshot = new ReferenceFileSnapshot("sources_items", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byInternalName.Count);
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

    private void ParseAndSwapQuests(IReadOnlyDictionary<string, PocoQuest> raw, ReferenceFileMetadata meta)
    {
        var byKey = new Dictionary<string, QuestEntry>(raw.Count, StringComparer.Ordinal);
        var byName = new Dictionary<string, QuestEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, v) in raw)
        {
            var objectives = (v.Objectives ?? (IReadOnlyList<PocoQuestObjective>)[])
                .Where(o => !string.IsNullOrEmpty(o.Type))
                .Select(o => new QuestObjective(
                    o.Type!,
                    o.Description ?? "",
                    o.Number,
                    o.Target is { Count: > 0 } t ? string.Join(" | ", t) : null,
                    o.ItemName,
                    o.GroupId))
                .ToList();

            var requirements = (v.Requirements ?? (IReadOnlyList<PocoQuestRequirement>)[])
                .Select(ProjectQuestRequirement)
                .ToList();

            var sustainList = v.RequirementsToSustain ?? (IReadOnlyList<PocoQuestRequirement>)[];
            var sustain = sustainList.Count > 0 ? ProjectQuestRequirement(sustainList[0]) : null;

            var skillRewards = (v.Rewards ?? (IReadOnlyList<Mithril.Reference.Models.Quests.QuestReward>)[])
                .OfType<Mithril.Reference.Models.Quests.SkillXpReward>()
                .Where(r => !string.IsNullOrEmpty(r.Skill))
                .Select(r => new QuestSkillReward(r.Skill!, r.Xp ?? 0))
                .ToList();

            var itemRewards = (v.Rewards_Items ?? (IReadOnlyList<Mithril.Reference.Models.Quests.QuestItemRef>)[])
                .Where(i => !string.IsNullOrEmpty(i.Item))
                .Select(i => new QuestItemReward(i.Item!, i.StackSize))
                .ToList();

            var entry = new QuestEntry(
                Key: key,
                Name: v.Name ?? "",
                InternalName: v.InternalName ?? "",
                Description: v.Description ?? "",
                DisplayedLocation: string.IsNullOrEmpty(v.DisplayedLocation) ? null : v.DisplayedLocation,
                FavorNpc: string.IsNullOrEmpty(v.FavorNpc) ? null : v.FavorNpc,
                Keywords: v.Keywords ?? (IReadOnlyList<string>)[],
                Objectives: objectives,
                Requirements: requirements,
                RequirementsToSustain: sustain,
                SkillRewards: skillRewards,
                ItemRewards: itemRewards,
                FavorReward: v.Reward_Favor ?? v.Rewards_Favor ?? 0,
                RewardEffects: v.Rewards_Effects ?? (IReadOnlyList<string>)[],
                RewardLootProfile: string.IsNullOrEmpty(v.Rewards_NamedLootProfile) ? null : v.Rewards_NamedLootProfile,
                ReuseMinutes: v.ReuseTime_Minutes,
                ReuseHours: v.ReuseTime_Hours,
                ReuseDays: v.ReuseTime_Days,
                PrefaceText: string.IsNullOrEmpty(v.PrefaceText) ? null : v.PrefaceText,
                SuccessText: string.IsNullOrEmpty(v.SuccessText) ? null : v.SuccessText);

            byKey[key] = entry;
            if (!string.IsNullOrEmpty(entry.InternalName)) byName[entry.InternalName] = entry;
        }
        _quests = byKey;
        _questsByInternalName = byName;
        _questsSnapshot = new ReferenceFileSnapshot("quests", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byKey.Count);
    }

    /// <summary>
    /// Project a polymorphic <see cref="PocoQuestRequirement"/> subclass to the
    /// flat <see cref="QuestRequirement"/> projection record. Reads only the
    /// discriminator-relevant fields per concrete type; the rest stay null.
    /// </summary>
    private static QuestRequirement ProjectQuestRequirement(PocoQuestRequirement r) => r switch
    {
        Mithril.Reference.Models.Quests.MinSkillLevelRequirement m =>
            new QuestRequirement(r.T, null, m.Level, null, m.Skill, null),
        Mithril.Reference.Models.Quests.MinFavorLevelRequirement m =>
            new QuestRequirement(r.T, null, m.Level, m.Npc, null, null),
        Mithril.Reference.Models.Quests.QuestCompletedRequirement q =>
            new QuestRequirement(r.T, q.Quest, null, null, null, null),
        Mithril.Reference.Models.Quests.HasEffectKeywordRequirement h =>
            new QuestRequirement(r.T, null, null, null, null, h.Keyword),
        Mithril.Reference.Models.Quests.MinCombatSkillLevelRequirement c =>
            new QuestRequirement(r.T, null, c.Level?.ToString(), null, null, null),
        _ => new QuestRequirement(r.T, null, null, null, null, null),
    };

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
