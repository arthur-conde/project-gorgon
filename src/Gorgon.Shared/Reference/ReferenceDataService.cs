using System.IO;
using System.Net.Http;
using System.Text.Json;
using Gorgon.Shared.Diagnostics;

namespace Gorgon.Shared.Reference;

public sealed class ReferenceDataService : IReferenceDataService
{
    public const string CdnRoot = "https://cdn.projectgorgon.com/";
    public const string FallbackCdnVersion = "v469";

    private readonly string _cacheDir;
    private readonly string _bundledDir;
    private readonly HttpClient _http;
    private readonly IDiagnosticsSink? _diag;

    // Items
    private IReadOnlyDictionary<long, ItemEntry> _items = new Dictionary<long, ItemEntry>();
    private IReadOnlyDictionary<string, ItemEntry> _itemsByInternalName =
        new Dictionary<string, ItemEntry>(StringComparer.Ordinal);
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

    // Item sources (sources_items.json)
    private IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> _itemSources =
        new Dictionary<string, IReadOnlyList<ItemSource>>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _itemSourcesSnapshot;

    public ReferenceDataService(string cacheDir, HttpClient http, IDiagnosticsSink? diag = null, string? bundledDir = null)
    {
        _cacheDir = cacheDir;
        _http = http;
        _diag = diag;
        _bundledDir = bundledDir ?? Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");

        _itemsSnapshot = new ReferenceFileSnapshot("items", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _recipesSnapshot = new ReferenceFileSnapshot("recipes", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _skillsSnapshot = new ReferenceFileSnapshot("skills", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _xpTablesSnapshot = new ReferenceFileSnapshot("xptables", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _npcsSnapshot = new ReferenceFileSnapshot("npcs", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        _itemSourcesSnapshot = new ReferenceFileSnapshot("sources_items", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);

        LoadItems();
        LoadRecipes();
        LoadSkills();
        LoadXpTables();
        LoadNpcs();
        LoadItemSources();
    }

    public IReadOnlyList<string> Keys { get; } = ["items", "recipes", "skills", "xptables", "npcs", "sources_items"];

    public IReadOnlyDictionary<long, ItemEntry> Items => _items;
    public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName => _itemsByInternalName;
    public IReadOnlyDictionary<string, RecipeEntry> Recipes => _recipes;
    public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName => _recipesByInternalName;
    public IReadOnlyDictionary<string, SkillEntry> Skills => _skills;
    public IReadOnlyDictionary<string, XpTableEntry> XpTables => _xpTables;
    public IReadOnlyDictionary<string, NpcEntry> Npcs => _npcs;
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources => _itemSources;

    public ReferenceFileSnapshot GetSnapshot(string key) => key switch
    {
        "items" => _itemsSnapshot,
        "recipes" => _recipesSnapshot,
        "skills" => _skillsSnapshot,
        "xptables" => _xpTablesSnapshot,
        "npcs" => _npcsSnapshot,
        "sources_items" => _itemSourcesSnapshot,
        _ => throw new ArgumentException($"Unknown reference file key: {key}", nameof(key)),
    };

    public event EventHandler<string>? FileUpdated;

    public Task RefreshAsync(string key, CancellationToken ct = default) => key switch
    {
        "items" => RefreshFileAsync("items", ReferenceJsonContext.Default.DictionaryStringRawItem, ParseAndSwapItems, ct),
        "recipes" => RefreshFileAsync("recipes", ReferenceJsonContext.Default.DictionaryStringRawRecipe, ParseAndSwapRecipes, ct),
        "skills" => RefreshFileAsync("skills", ReferenceJsonContext.Default.DictionaryStringRawSkill, ParseAndSwapSkills, ct),
        "xptables" => RefreshFileAsync("xptables", ReferenceJsonContext.Default.DictionaryStringRawXpTable, ParseAndSwapXpTables, ct),
        "npcs" => RefreshFileAsync("npcs", ReferenceJsonContext.Default.DictionaryStringRawNpc, ParseAndSwapNpcs, ct),
        "sources_items" => RefreshFileAsync("sources_items", ReferenceJsonContext.Default.DictionaryStringRawItemSourceEnvelope, ParseAndSwapItemSources, ct),
        _ => throw new ArgumentException($"Unknown reference file key: {key}", nameof(key)),
    };

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await RefreshAsync("items", ct);
        await RefreshAsync("recipes", ct);
        await RefreshAsync("skills", ct);
        await RefreshAsync("xptables", ct);
        await RefreshAsync("npcs", ct);
        await RefreshAsync("sources_items", ct);
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

    private void LoadFile<TRaw>(
        string fileName,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<Dictionary<string, TRaw>> typeInfo,
        Action<Dictionary<string, TRaw>, ReferenceFileMetadata> swapper)
    {
        var cachePath = Path.Combine(_cacheDir, $"{fileName}.json");
        var cacheMetaPath = Path.Combine(_cacheDir, $"{fileName}.meta.json");

        if (File.Exists(cachePath))
        {
            try
            {
                var meta = TryLoadMetadata(cacheMetaPath, ReferenceFileSource.Cache);
                using var stream = File.OpenRead(cachePath);
                var raw = JsonSerializer.Deserialize(stream, typeInfo) ?? new Dictionary<string, TRaw>();
                swapper(raw, meta);
                _diag?.Info("Reference", $"Loaded {fileName} from cache ({raw.Count} raw entries, {meta.CdnVersion}).");
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
        using var bundledStream = File.OpenRead(bundledPath);
        var bundledRaw = JsonSerializer.Deserialize(bundledStream, typeInfo) ?? new Dictionary<string, TRaw>();
        swapper(bundledRaw, bundledMeta);
        _diag?.Info("Reference", $"Loaded {fileName} from bundled ({bundledRaw.Count} raw entries, {bundledMeta.CdnVersion}).");
    }

    private async Task RefreshFileAsync<TRaw>(
        string fileName,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<Dictionary<string, TRaw>> typeInfo,
        Action<Dictionary<string, TRaw>, ReferenceFileMetadata> swapper,
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
        var tmp = cachePath + ".tmp";
        await File.WriteAllBytesAsync(tmp, body, ct);
        File.Move(tmp, cachePath, overwrite: true);

        var metaTmp = metaPath + ".tmp";
        await using (var ms = File.Create(metaTmp))
        {
            await JsonSerializer.SerializeAsync(ms, meta, ReferenceJsonContext.Default.ReferenceFileMetadata, ct);
        }
        File.Move(metaTmp, metaPath, overwrite: true);

        var raw = JsonSerializer.Deserialize(body, typeInfo) ?? new Dictionary<string, TRaw>();
        swapper(raw, meta);
        _diag?.Info("Reference", $"{fileName}.json refreshed: version {version}.");
        FileUpdated?.Invoke(this, fileName);
    }

    // ── Per-type load entry points ───────────────────────────────────────

    private void LoadItems() => LoadFile("items", ReferenceJsonContext.Default.DictionaryStringRawItem, ParseAndSwapItems);
    private void LoadRecipes() => LoadFile("recipes", ReferenceJsonContext.Default.DictionaryStringRawRecipe, ParseAndSwapRecipes);
    private void LoadSkills() => LoadFile("skills", ReferenceJsonContext.Default.DictionaryStringRawSkill, ParseAndSwapSkills);
    private void LoadXpTables() => LoadFile("xptables", ReferenceJsonContext.Default.DictionaryStringRawXpTable, ParseAndSwapXpTables);
    private void LoadNpcs() => LoadFile("npcs", ReferenceJsonContext.Default.DictionaryStringRawNpc, ParseAndSwapNpcs);
    private void LoadItemSources() => LoadFile("sources_items", ReferenceJsonContext.Default.DictionaryStringRawItemSourceEnvelope, ParseAndSwapItemSources);

    // ── Per-type parse-and-swap ──────────────────────────────────────────

    private void ParseAndSwapItems(Dictionary<string, RawItem> raw, ReferenceFileMetadata meta)
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
            var entry = new ItemEntry(id, v.Name ?? "", v.InternalName ?? "", v.MaxStackSize ?? 1, v.IconId ?? 0,
                keywords, v.EquipSlot, skillPrereqs, v.Value ?? 0, v.FoodDesc, v.SkillReqs);
            byId[id] = entry;
            if (!string.IsNullOrEmpty(entry.InternalName)) byName[entry.InternalName] = entry;
        }
        _items = byId;
        _itemsByInternalName = byName;
        _itemsSnapshot = new ReferenceFileSnapshot("items", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byId.Count);
    }

    /// <summary>
    /// Parses raw keyword strings and synthesizes virtual keywords from item metadata.
    /// NPC preferences use filter keywords like "SkillPrereq:Archery", "EquipmentSlot:Head",
    /// "MinRarity:Rare", "MinValue:1000" that don't appear in item keyword arrays directly.
    /// We synthesize these as additional ItemKeyword entries so the GiftIndex can match them.
    /// </summary>
    private static IReadOnlyList<ItemKeyword> ParseKeywords(
        List<string>? raw, string? equipSlot, List<string>? skillPrereqs, decimal? value)
    {
        var result = new List<ItemKeyword>(raw?.Count ?? 0 + 4);

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
        if (value.HasValue)
        {
            var v = (int)value.Value;
            if (v >= 1000) result.Add(new ItemKeyword("MinValue:1000", 0));
            if (v >= 500) result.Add(new ItemKeyword("MinValue:500", 0));
        }

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

    private void ParseAndSwapRecipes(Dictionary<string, RawRecipe> raw, ReferenceFileMetadata meta)
    {
        var byKey = new Dictionary<string, RecipeEntry>(raw.Count, StringComparer.Ordinal);
        var byName = new Dictionary<string, RecipeEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, v) in raw)
        {
            var ingredients = v.Ingredients?
                .Where(i => i.ItemCode.HasValue)
                .Select(i => new RecipeItemRef(i.ItemCode!.Value, i.StackSize ?? 1, i.ChanceToConsume))
                .ToList()
                ?? (IReadOnlyList<RecipeItemRef>)[];

            var results = v.ResultItems?
                .Where(i => i.ItemCode.HasValue)
                .Select(i => new RecipeItemRef(i.ItemCode!.Value, i.StackSize ?? 1, null))
                .ToList()
                ?? (IReadOnlyList<RecipeItemRef>)[];

            var entry = new RecipeEntry(
                key,
                v.Name ?? "",
                v.InternalName ?? "",
                v.IconId ?? 0,
                v.Skill ?? "",
                v.SkillLevelReq ?? 0,
                v.RewardSkill ?? "",
                v.RewardSkillXp ?? 0,
                v.RewardSkillXpFirstTime ?? 0,
                v.RewardSkillXpDropOffLevel,
                v.RewardSkillXpDropOffPct,
                v.RewardSkillXpDropOffRate,
                ingredients,
                results,
                v.PrereqRecipe);
            byKey[key] = entry;
            if (!string.IsNullOrEmpty(entry.InternalName)) byName[entry.InternalName] = entry;
        }
        _recipes = byKey;
        _recipesByInternalName = byName;
        _recipesSnapshot = new ReferenceFileSnapshot("recipes", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byKey.Count);
    }

    private void ParseAndSwapSkills(Dictionary<string, RawSkill> raw, ReferenceFileMetadata meta)
    {
        var byName = new Dictionary<string, SkillEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, v) in raw)
        {
            var entry = new SkillEntry(key, v.Id ?? 0, v.Combat ?? false, v.XpTable ?? "", v.MaxBonusLevels ?? 0);
            byName[key] = entry;
        }
        _skills = byName;
        _skillsSnapshot = new ReferenceFileSnapshot("skills", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byName.Count);
    }

    private void ParseAndSwapXpTables(Dictionary<string, RawXpTable> raw, ReferenceFileMetadata meta)
    {
        var byName = new Dictionary<string, XpTableEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (_, v) in raw)
        {
            if (string.IsNullOrEmpty(v.InternalName)) continue;
            var entry = new XpTableEntry(v.InternalName, (IReadOnlyList<long>)(v.XpAmounts ?? []));
            byName[v.InternalName] = entry;
        }
        _xpTables = byName;
        _xpTablesSnapshot = new ReferenceFileSnapshot("xptables", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byName.Count);
    }

    private void ParseAndSwapNpcs(Dictionary<string, RawNpc> raw, ReferenceFileMetadata meta)
    {
        var byKey = new Dictionary<string, NpcEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, v) in raw)
        {
            var prefs = (v.Preferences ?? [])
                .Where(p => p.Keywords is { Count: > 0 })
                .Select(p => new NpcPreference(
                    p.Desire ?? "",
                    (IReadOnlyList<string>)(p.Keywords ?? []),
                    p.Name ?? string.Join(", ", p.Keywords ?? []),
                    p.Pref ?? 0,
                    p.Favor))
                .ToList();

            var services = (v.Services ?? [])
                .Where(s => !string.IsNullOrEmpty(s.Type))
                .Select(s => new NpcService(
                    s.Type!,
                    s.Favor,
                    ParseCapIncreases(s.CapIncreases)))
                .ToList();

            var entry = new NpcEntry(
                key,
                v.Name ?? key.Replace("NPC_", ""),
                v.AreaFriendlyName ?? "",
                prefs,
                (IReadOnlyList<string>)(v.ItemGifts ?? []),
                services);

            byKey[key] = entry;
        }
        _npcs = byKey;
        _npcsSnapshot = new ReferenceFileSnapshot("npcs", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byKey.Count);
    }

    /// <summary>Parses <c>"Despised:5000:Armor,Weapon,CorpseTrophy"</c> strings.</summary>
    private static IReadOnlyList<NpcStoreCapIncrease> ParseCapIncreases(List<string>? raw)
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

    private void ParseAndSwapItemSources(Dictionary<string, RawItemSourceEnvelope> raw, ReferenceFileMetadata meta)
    {
        // sources_items.json shape: { "item_N": { "entries": [ { npc, type, ... }, ... ] } }
        var byInternalName = new Dictionary<string, IReadOnlyList<ItemSource>>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, envelope) in raw)
        {
            var underscore = key.IndexOf('_');
            if (underscore < 0) continue;
            if (!long.TryParse(key.AsSpan(underscore + 1), out var id)) continue;
            if (!_items.TryGetValue(id, out var item) || string.IsNullOrEmpty(item.InternalName)) continue;
            if (envelope.Entries is null || envelope.Entries.Count == 0) continue;

            var projected = new List<ItemSource>(envelope.Entries.Count);
            foreach (var r in envelope.Entries)
            {
                if (string.IsNullOrEmpty(r.Type)) continue;
                var context = r.Recipe ?? r.Quest ?? r.Monster ?? r.Source ?? r.Interactor;
                projected.Add(new ItemSource(r.Type!, r.Npc, context));
            }
            if (projected.Count > 0)
                byInternalName[item.InternalName] = projected;
        }
        _itemSources = byInternalName;
        _itemSourcesSnapshot = new ReferenceFileSnapshot("sources_items", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byInternalName.Count);
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
