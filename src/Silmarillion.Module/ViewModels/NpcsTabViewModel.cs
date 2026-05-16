using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.Query;
// Disambiguate the POCO type from the slim Mithril.Shared.Reference.NpcService projection
// (the latter is consumed by Arwen for gift calculation).
using NpcServicePoco = Mithril.Reference.Models.Npcs.NpcService;

namespace Silmarillion.ViewModels;

/// <summary>
/// NPCs master-detail view-model. Mirrors <see cref="ItemsTabViewModel"/> / <see cref="RecipesTabViewModel"/>'s
/// shape: filterable row list on the left, NPC detail on the right. On selection change
/// builds an <see cref="NpcDetailViewModel"/> with service rows, "Teaches recipes" / "Sells items"
/// cross-link chips, "Quests" link list, and gift-preference rows resolved from
/// <see cref="IReferenceDataService"/>.
///
/// Subscribes to <see cref="IReferenceDataService.FileUpdated"/> for <c>"npcs"</c>,
/// <c>"items"</c>, <c>"recipes"</c>, <c>"sources_items"</c>, <c>"sources_recipes"</c>, and
/// <c>"quests"</c> (any of which can change either the master list or one of the cross-link
/// sections) and rebuilds <see cref="AllNpcs"/> on the UI thread, preserving the current
/// selection by <see cref="NpcListRow.InternalName"/>.
/// </summary>
public sealed partial class NpcsTabViewModel : ObservableObject, ITabViewModel
{
    /// <summary>
    /// Reflected schema for <see cref="NpcListRow"/> exposed to <c>MithrilQueryBox.Schema</c>
    /// so the query box can offer completion and highlight known column names. <c>QueryFilter</c>
    /// on the bound ListBox reflects the same surface from the item type at attach time, so the
    /// suggestions stay in sync with what actually filters.
    /// </summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(NpcListRow)));

    public string TabHeader => "NPCs";
    public int TabOrder => 2;

    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;
    private readonly IEntityNameResolver _nameResolver;
    private readonly RelayCommand<EntityRef?> _openEntityCommand;

    public NpcsTabViewModel(IReferenceDataService refData, IReferenceNavigator navigator, IEntityNameResolver nameResolver)
    {
        _refData = refData;
        _navigator = navigator;
        _nameResolver = nameResolver;
        _openEntityCommand = new RelayCommand<EntityRef?>(r => { if (r is not null) _navigator.Open(r); });
        _allNpcs = BuildAllNpcs(refData);
        refData.FileUpdated += OnFileUpdated;
    }

    [ObservableProperty]
    private IReadOnlyList<NpcListRow> _allNpcs;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    /// <summary>
    /// Non-fatal narrowing diagnostics from the last compiled query (e.g. an
    /// optional-hierarchy single-subtype field referenced without a discriminator
    /// guard). Distinct from <see cref="QueryError"/> — the query still ran. Wired
    /// from <c>QueryFilter.QueryWarning</c> OneWayToSource; surfaced as an amber hint.
    /// </summary>
    [ObservableProperty]
    private string? _queryWarning;

    /// <summary>
    /// ListBox-bound selection. Setting it from an <see cref="Npc"/> POCO (in tests or via the
    /// navigator) resolves to the matching row.
    /// </summary>
    [ObservableProperty]
    private NpcListRow? _selectedRow;

    [ObservableProperty]
    private NpcDetailViewModel? _detailViewModel;

    partial void OnSelectedRowChanged(NpcListRow? value)
    {
        if (value is null)
        {
            DetailViewModel = null;
            return;
        }

        DetailViewModel = BuildDetailViewModel(value);
    }

    private void OnFileUpdated(object? sender, string fileKey)
    {
        // npcs.json drives the master list. items/recipes/sources_* affect the cross-link
        // sections on the detail pane — rebuild on each so an open detail re-resolves chips
        // against the fresh refData snapshot. quests.json feeds the (plain-text) Quests block.
        if (fileKey is not ("npcs" or "items" or "recipes" or "sources_items" or "sources_recipes" or "quests" or "abilities" or "sources_abilities"))
            return;

        UiThread.Run(() =>
        {
            var captured = SelectedRow?.InternalName;
            if (fileKey == "npcs")
            {
                AllNpcs = BuildAllNpcs(_refData);
            }
            if (!string.IsNullOrEmpty(captured))
            {
                var resolved = AllNpcs.FirstOrDefault(r => r.InternalName == captured);
                // Toggle through null to force OnSelectedRowChanged to rebuild the detail
                // VM with the fresh refData snapshot (cross-link resolution uses live refData).
                SelectedRow = null;
                SelectedRow = resolved;
            }
        });
    }

    private IReadOnlyList<NpcListRow> BuildAllNpcs(IReferenceDataService refData) =>
        refData.NpcsByInternalName
            .Select(kv => BuildRow(kv.Key, kv.Value))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private NpcListRow BuildRow(string internalName, Npc npc) => new(
        Npc: npc,
        InternalName: internalName,
        Name: _nameResolver.Resolve(EntityRef.Npc(internalName)),
        AreaName: npc.AreaName ?? "",
        AreaDisplayName: npc.AreaFriendlyName ?? npc.AreaName ?? "(unknown)",
        ServiceTypes: BuildServiceTypes(npc),
        CapIncreases: BuildCapIncreases(npc));

    /// <summary>
    /// Flatten every <see cref="StoreService"/>'s parsed cap-increase rows into one
    /// homogeneous list on the row so the query box can ask the originating #349
    /// question (<c>CapIncreases WITH ANY (Tier = 'Friends' AND GoldCap &gt; 1000)</c>).
    /// Parsing lives in <see cref="StoreCapIncreaseParser"/> (#350) — this is a pure
    /// projection. NPCs with no store service yield an empty list (the engine's
    /// quantifier treats that as no-match for <c>ANY</c>, vacuously true for <c>ALL</c>).
    /// </summary>
    private static IReadOnlyList<StoreCapIncrease> BuildCapIncreases(Npc npc)
    {
        if (npc.Services is null || npc.Services.Count == 0) return [];
        return npc.Services
            .OfType<StoreService>()
            .SelectMany(s => s.ParsedCapIncreases)
            .ToList();
    }

    private static IReadOnlyList<NpcServiceTypeValue> BuildServiceTypes(Npc npc)
    {
        if (npc.Services is null || npc.Services.Count == 0) return [];
        return npc.Services
            .Where(s => !string.IsNullOrEmpty(s.Type))
            .Select(s => s.Type)
            .Distinct(StringComparer.Ordinal)
            .Select(t => new NpcServiceTypeValue(t))
            .ToList();
    }

    private NpcDetailViewModel BuildDetailViewModel(NpcListRow row)
    {
        var services = BuildServiceRows(row.Npc);
        var taught = BuildTaughtRecipeChips(row.InternalName);
        var taughtAbilities = BuildTaughtAbilityChips(row.InternalName);
        var sold = BuildSoldItemChips(row.InternalName);
        var quests = BuildQuestLinks(row.InternalName);
        var preferences = BuildPreferenceRows(row.Npc);
        var giftTiers = row.Npc.ItemGifts ?? (IReadOnlyList<string>)[];
        var areaChip = BuildAreaChip(row.Npc);

        return new NpcDetailViewModel(
            row.Npc,
            row.InternalName,
            _nameResolver,
            services,
            taught,
            taughtAbilities,
            sold,
            quests,
            preferences,
            giftTiers,
            areaChip,
            _openEntityCommand);
    }

    /// <summary>
    /// Build the navigable area chip for an NPC's home area. Uses <see cref="Npc.AreaName"/>
    /// (envelope-key form, e.g. <c>"AreaSerbule"</c>) as the navigation key and
    /// <see cref="Npc.AreaFriendlyName"/> as the display label, falling back to AreaName when
    /// FriendlyName is missing. <c>IsNavigable</c> is gated by the navigator's
    /// <c>CanOpen</c> — the chip degrades to plain text when the Areas tab isn't registered
    /// (per cookbook *Cross-link chips → audit existing surfaces*; flips to clickable the
    /// moment Areas ships, #245).
    /// </summary>
    private EntityChipVm? BuildAreaChip(Npc npc)
    {
        if (string.IsNullOrEmpty(npc.AreaName)) return null;
        var reference = EntityRef.Area(npc.AreaName);
        var label = string.IsNullOrEmpty(npc.AreaFriendlyName) ? npc.AreaName : npc.AreaFriendlyName;
        return new EntityChipVm(
            DisplayName: label,
            IconId: 0,
            Reference: reference,
            IsNavigable: _navigator.CanOpen(reference));
    }

    private IReadOnlyList<NpcServiceRow> BuildServiceRows(Npc npc)
    {
        if (npc.Services is null || npc.Services.Count == 0) return [];
        return npc.Services
            .Where(s => !string.IsNullOrEmpty(s.Type))
            .Select(s => new NpcServiceRow(
                s.Type,
                // "Despised" is the lowest favor tier — i.e. anyone can access the service.
                // Showing a "Favor: Despised" badge on every row is pure noise; null it out
                // so the XAML hides the chip and reserves the badge for meaningful gates.
                MinFavorTier: string.Equals(s.Favor, "Despised", StringComparison.Ordinal) ? null : s.Favor,
                BuildServiceDetails(s)))
            .ToList();
    }

    /// <summary>
    /// Flatten each NpcService subclass to display-relevant payload lines. Lines are pre-labeled
    /// per sub-list (e.g. <c>"Skills: Unarmed, Lore"</c> / <c>"Unlocks at higher favor: Neutral,
    /// Comfortable, ..."</c>) so the detail view can render them in a single linear strip and the
    /// viewer can tell skill names apart from favor-tier unlocks. Store cap-increase rows carry
    /// a chip strip on top of the prose (per-tier keyword tuple as <see cref="EntityKind.ItemKeyword"/>
    /// chips); other rows are text-only.
    /// </summary>
    private IReadOnlyList<NpcServiceDetailLine> BuildServiceDetails(NpcServicePoco service) => service switch
    {
        // Store caps are kept one-per-row: each tier raises a distinct gold cap and the per-row
        // keyword tuple is surfaced as a chip strip that flips to the Items tab pre-filtered.
        StoreService store when store.CapIncreases is { Count: > 0 } =>
            store.ParsedCapIncreases.Select(FormatCapIncrease).ToList(),

        BarterService barter when barter.AdditionalUnlocks is { Count: > 0 } =>
            BuildLabeledLines(("Unlocks at higher favor", barter.AdditionalUnlocks)),

        // Consignment.ItemTypes is a raw item-keyword tuple (Equipment, CorpseTrophy, …) — same
        // shape as Store cap-increase keywords from PR #294, so lift it into a chip strip via
        // BuildKeywordChips. Unlocks are favor tiers and stay text-only.
        ConsignmentService consignment =>
            BuildConsignmentDetails(consignment),

        InstallAugmentsService install when install.LevelRange is { Count: > 0 } =>
            BuildLabeledLines(("Levels", install.LevelRange)),

        StorageService storage =>
            BuildLabeledLines(
                ("Items", storage.ItemDescs),
                ("Space increases at", storage.SpaceIncreases)),

        // Skills are raw skills.json keys (e.g. "NonfictionWriting"); resolve through the
        // entity-name resolver so PascalCase keys render with their friendly DisplayName
        // ("Non-Fiction Writing"). Single-word keys ("Toolcrafting", "Carpentry") pass through
        // unchanged because their key matches their display name.
        TrainingService training =>
            BuildLabeledLines(
                ("Skills", training.Skills?.Select(s => _nameResolver.Resolve(EntityRef.Skill(s))).ToList()),
                ("Unlocks at higher favor", training.Unlocks)),

        _ => [],
    };

    /// <summary>
    /// Join each non-empty sub-list into a single labeled line so Skills and Unlocks (etc.) stay
    /// visually distinct in the detail pane. Empty groups drop out entirely.
    /// </summary>
    private static IReadOnlyList<NpcServiceDetailLine> BuildLabeledLines(params (string Label, IReadOnlyList<string>? Items)[] groups)
    {
        var lines = new List<NpcServiceDetailLine>(groups.Length);
        foreach (var (label, items) in groups)
        {
            if (items is null || items.Count == 0) continue;
            lines.Add(NpcServiceDetailLine.TextOnly($"{label}: {string.Join(", ", items)}"));
        }
        return lines;
    }

    /// <summary>
    /// Build the Consignment service detail lines. The <c>Items</c> row carries a label-only prose
    /// (<c>"Items:"</c>) plus a per-line keyword chip strip — each chip targets the Items tab via
    /// <see cref="EntityKind.ItemKeyword"/>, mirroring the Store cap-keyword pattern from PR #294.
    /// Empty groups drop out so a Consignment with only <c>Unlocks</c> (or only <c>ItemTypes</c>)
    /// still renders cleanly.
    /// </summary>
    private IReadOnlyList<NpcServiceDetailLine> BuildConsignmentDetails(ConsignmentService consignment)
    {
        var lines = new List<NpcServiceDetailLine>(2);
        if (consignment.ItemTypes is { Count: > 0 } itemTypes)
            lines.Add(new NpcServiceDetailLine("Items:", BuildKeywordChips(itemTypes)));
        if (consignment.Unlocks is { Count: > 0 } unlocks)
            lines.Add(NpcServiceDetailLine.TextOnly($"Unlocks at higher favor: {string.Join(", ", unlocks)}"));
        return lines;
    }

    /// <summary>
    /// Format one parsed Store <see cref="StoreCapIncrease"/> row. Prose is
    /// <c>"Tier → 5,000g"</c>; the keyword tuple (when present) becomes the per-line chip
    /// strip — each chip targets the Items tab via <see cref="EntityKind.ItemByKeyword"/>
    /// (the single-keyword Items filter pivot restored in #327; not the retired #270 fan-out
    /// kind). Parsing now lives in <see cref="StoreCapIncreaseParser"/> (#350) — this method
    /// is presentation only.
    /// </summary>
    private NpcServiceDetailLine FormatCapIncrease(StoreCapIncrease cap)
    {
        var gold = cap.GoldCap is { } g ? g.ToString("N0") + "g" : "—";
        var prose = $"{cap.Tier.DisplayName()} → {gold}";
        if (cap.Keywords.Count == 0)
            return NpcServiceDetailLine.TextOnly(prose);
        return new NpcServiceDetailLine(prose, BuildKeywordChips(cap.Keywords));
    }

    /// <summary>
    /// Wrap each Store-cap keyword tag as a navigable chip targeting the Items tab via
    /// <see cref="EntityKind.ItemByKeyword"/> (single-keyword 1:1 filter pivot, #327).
    /// Friendly chip labels come from
    /// <see cref="IReferenceDataService.KeywordDisplayNames"/>; unmapped tags fall back to a
    /// CamelCase split (matching the item-detail "Used as" chip behavior from PR #267).
    /// </summary>
    private IReadOnlyList<EntityChipVm> BuildKeywordChips(IReadOnlyList<string> keywordTags)
    {
        var displayNames = _refData.KeywordDisplayNames;
        var chips = new List<EntityChipVm>(keywordTags.Count);
        foreach (var tag in keywordTags)
        {
            var display = displayNames.TryGetValue(tag, out var friendly)
                ? friendly
                : CamelCaseSplitConverter.Split(tag);
            // #327: Store-cap / Consignment keyword chip is a single-keyword Items filter
            // pivot (1:1 per the #318 chip-vs-popup rule — one tag → "open the Items tab
            // filtered to this keyword"). Restored via the symmetric
            // EntityKind.ItemByKeyword (NOT the retired #270 ItemKeyword recipe-slot
            // fan-out kind); #326 had degraded it to non-navigable plain text when the
            // double-duty ItemKeyword kind was retired for its fan-out use.
            var reference = EntityRef.ItemByKeyword(tag);
            chips.Add(new EntityChipVm(
                DisplayName: display,
                IconId: 0,
                Reference: reference,
                IsNavigable: _navigator.CanOpen(reference)));
        }
        return chips;
    }

    private IReadOnlyList<EntityChipVm> BuildTaughtRecipeChips(string npcInternalName)
    {
        if (!_refData.RecipesTaughtByNpc.TryGetValue(npcInternalName, out var recipes) || recipes.Count == 0)
            return [];
        return recipes
            .OrderBy(r => r.Name ?? r.InternalName ?? r.Key, StringComparer.OrdinalIgnoreCase)
            .Select(r => new EntityChipVm(
                DisplayName: r.Name ?? r.InternalName ?? r.Key,
                IconId: r.IconId > 0 ? r.IconId : ResolveRecipeFallbackIcon(r),
                Reference: EntityRef.Recipe(r.InternalName ?? r.Key),
                IsNavigable: _navigator.CanOpen(EntityRef.Recipe(r.InternalName ?? r.Key))))
            .ToList();
    }

    private int ResolveRecipeFallbackIcon(Recipe recipe)
    {
        var source = (recipe.ResultItems is { Count: > 0 } ? recipe.ResultItems : recipe.ProtoResultItems)
            ?? (IReadOnlyList<RecipeResultItem>)Array.Empty<RecipeResultItem>();
        foreach (var result in source)
        {
            if (_refData.Items.TryGetValue(result.ItemCode, out var item) && item.IconId > 0)
                return item.IconId;
        }
        return 0;
    }

    private IReadOnlyList<EntityChipVm> BuildTaughtAbilityChips(string npcInternalName)
    {
        if (!_refData.AbilitiesTaughtByNpc.TryGetValue(npcInternalName, out var abilities) || abilities.Count == 0)
            return [];
        return abilities
            .Where(a => !string.IsNullOrEmpty(a.InternalName))
            .OrderBy(a => a.Skill ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Level)
            .ThenBy(a => a.Name ?? a.InternalName, StringComparer.OrdinalIgnoreCase)
            .Select(a =>
            {
                var reference = EntityRef.Ability(a.InternalName!);
                return new EntityChipVm(
                    DisplayName: _nameResolver.Resolve(reference),
                    IconId: a.IconID,
                    Reference: reference,
                    IsNavigable: _navigator.CanOpen(reference));
            })
            .ToList();
    }

    private IReadOnlyList<EntityChipVm> BuildSoldItemChips(string npcInternalName)
    {
        if (!_refData.ItemsSoldByNpc.TryGetValue(npcInternalName, out var items) || items.Count == 0)
            return [];
        return items
            .OrderBy(i => i.Name ?? i.InternalName ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(i => new EntityChipVm(
                DisplayName: i.Name ?? i.InternalName ?? "",
                IconId: i.IconId,
                Reference: EntityRef.Item(i.InternalName ?? ""),
                IsNavigable: _navigator.CanOpen(EntityRef.Item(i.InternalName ?? ""))))
            .ToList();
    }

    /// <summary>
    /// Quest cross-link list. Reads <see cref="IReferenceDataService.QuestsByGiverNpc"/> —
    /// merges <see cref="Quest.QuestNpc"/> and <see cref="Quest.FavorNpc"/>, so quests where the
    /// NPC is the giver, the turn-in, or the favor anchor all surface. Rendered as navigable
    /// <see cref="EntityChipVm"/> chips that route to the Quests tab via <see cref="EntityRef.Quest"/>.
    /// </summary>
    private IReadOnlyList<EntityChipVm> BuildQuestLinks(string npcInternalName)
    {
        if (!_refData.QuestsByGiverNpc.TryGetValue(npcInternalName, out var quests) || quests.Count == 0)
            return [];
        return quests
            .OrderBy(q => q.Name ?? q.InternalName ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(q =>
            {
                var internalName = q.InternalName ?? "";
                var reference = EntityRef.Quest(internalName);
                return new EntityChipVm(
                    DisplayName: q.Name ?? internalName,
                    IconId: 0,
                    Reference: reference,
                    IsNavigable: _navigator.CanOpen(reference));
            })
            .ToList();
    }

    private static IReadOnlyList<NpcPreferenceRow> BuildPreferenceRows(Npc npc)
    {
        if (npc.Preferences is null || npc.Preferences.Count == 0) return [];
        // Sort matches Arwen's order: Love → Like → Dislike → Hate, then by descending Pref.
        return npc.Preferences
            .OrderBy(p => p.Desire switch { "Love" => 0, "Like" => 1, "Dislike" => 2, "Hate" => 3, _ => 4 })
            .ThenByDescending(p => p.Pref)
            .Select(p => new NpcPreferenceRow(
                Desire: p.Desire ?? "",
                DisplayName: p.Name ?? string.Join(", ", p.Keywords ?? (IReadOnlyList<string>)[]),
                Pref: p.Pref,
                Keywords: p.Keywords ?? (IReadOnlyList<string>)[],
                MinFavorTier: p.Favor))
            .ToList();
    }
}
