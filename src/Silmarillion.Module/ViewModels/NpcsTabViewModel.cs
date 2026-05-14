using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        if (fileKey is not ("npcs" or "items" or "recipes" or "sources_items" or "sources_recipes" or "quests"))
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
        AreaDisplayName: npc.AreaFriendlyName ?? npc.AreaName ?? "(unknown)",
        ServiceTypes: BuildServiceTypes(npc));

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
        var sold = BuildSoldItemChips(row.InternalName);
        var quests = BuildQuestLinks(row.InternalName);
        var preferences = BuildPreferenceRows(row.Npc);
        var giftTiers = row.Npc.ItemGifts ?? (IReadOnlyList<string>)[];

        return new NpcDetailViewModel(
            row.Npc,
            row.InternalName,
            _nameResolver,
            services,
            taught,
            sold,
            quests,
            preferences,
            giftTiers,
            _openEntityCommand);
    }

    private static IReadOnlyList<NpcServiceRow> BuildServiceRows(Npc npc)
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
    /// viewer can tell skill names apart from favor-tier unlocks. Plain strings — richer per-row
    /// chips are a #229-style polish follow-up.
    /// </summary>
    private static IReadOnlyList<string> BuildServiceDetails(NpcServicePoco service) => service switch
    {
        // Store caps are kept one-per-row: each tier raises a distinct gold cap and the per-row
        // keyword tuple disambiguates which category the cap applies to.
        StoreService store when store.CapIncreases is { Count: > 0 } =>
            store.CapIncreases.Select(FormatCapIncrease).ToList(),

        BarterService barter when barter.AdditionalUnlocks is { Count: > 0 } =>
            BuildLabeledLines(("Unlocks at higher favor", barter.AdditionalUnlocks)),

        ConsignmentService consignment =>
            BuildLabeledLines(
                ("Items", consignment.ItemTypes),
                ("Unlocks at higher favor", consignment.Unlocks)),

        InstallAugmentsService install when install.LevelRange is { Count: > 0 } =>
            BuildLabeledLines(("Levels", install.LevelRange)),

        StorageService storage =>
            BuildLabeledLines(
                ("Items", storage.ItemDescs),
                ("Space increases at", storage.SpaceIncreases)),

        TrainingService training =>
            BuildLabeledLines(
                ("Skills", training.Skills),
                ("Unlocks at higher favor", training.Unlocks)),

        _ => [],
    };

    /// <summary>
    /// Join each non-empty sub-list into a single labeled line so Skills and Unlocks (etc.) stay
    /// visually distinct in the detail pane. Empty groups drop out entirely.
    /// </summary>
    private static IReadOnlyList<string> BuildLabeledLines(params (string Label, IReadOnlyList<string>? Items)[] groups)
    {
        var lines = new List<string>(groups.Length);
        foreach (var (label, items) in groups)
        {
            if (items is null || items.Count == 0) continue;
            lines.Add($"{label}: {string.Join(", ", items)}");
        }
        return lines;
    }

    /// <summary>
    /// Format one Store cap-increase entry as <c>"Tier → 5,000g (Kw1, Kw2)"</c>, falling back to
    /// the tier + gold form when no keywords are listed. Mirrors the parser shape in
    /// <see cref="Mithril.Shared.Reference.ReferenceDataService"/> (the colon-separated raw form
    /// is already decomposed into tier/gold/keywords by the time it reaches the slim NpcEntry,
    /// but we're rendering off the raw POCO here, so we re-split.).
    /// </summary>
    private static string FormatCapIncrease(string raw)
    {
        // Raw line shape: "Despised:5000:Armor,Weapon,CorpseTrophy". Three colon-separated parts;
        // last part may be empty.
        var parts = raw.Split(':', 3);
        if (parts.Length < 2) return raw;
        var tier = parts[0];
        var gold = int.TryParse(parts[1], out var g) ? g.ToString("N0") + "g" : parts[1];
        if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[2])) return $"{tier} → {gold}";
        var keywords = parts[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return keywords.Length == 0 ? $"{tier} → {gold}" : $"{tier} → {gold} ({string.Join(", ", keywords)})";
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
