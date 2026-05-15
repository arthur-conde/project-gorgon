using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.Query;

namespace Silmarillion.ViewModels;

/// <summary>
/// Items master-detail view-model. Left side: filterable card list of every item in the
/// reference catalogue. Right side: <see cref="ItemDetailViewModel"/> for the current
/// <see cref="SelectedItem"/>, built lazily on selection change with cross-link sections
/// populated from <c>IReferenceDataService.RecipesByProducedItem</c> /
/// <c>RecipesByIngredientItem</c> / <c>ItemSources</c>.
///
/// Subscribes to <see cref="IReferenceDataService.FileUpdated"/> for <c>"items"</c> and
/// rebuilds <see cref="AllItems"/> on the UI thread, preserving the current selection by
/// <see cref="Item.InternalName"/>. Without this, a background CDN refresh after the tab
/// is loaded would leave WPF's ListBox bound to a stale Item collection while refData
/// hands out new instances — selections set via the navigator would silently fall out of
/// the list.
/// </summary>
public sealed partial class ItemsTabViewModel : ObservableObject, ITabViewModel
{
    /// <summary>
    /// Reflected schema for <see cref="Item"/> exposed to <c>MithrilQueryBox.Schema</c> so the
    /// query box can offer completion and highlight known column names. <c>QueryFilter</c> on
    /// the bound ListBox reflects the same surface from the item type at attach time, so the
    /// suggestions stay in sync with what actually filters.
    /// </summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(Item)));

    public string TabHeader => "Items";
    public int TabOrder => 0;

    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;
    private readonly IEntityNameResolver _nameResolver;
    private readonly SilmarillionSettings _settings;
    private readonly RelayCommand<EntityRef?> _openEntityCommand;

    public ItemsTabViewModel(IReferenceDataService refData, IReferenceNavigator navigator, IEntityNameResolver nameResolver)
        : this(refData, navigator, nameResolver, settings: null)
    {
    }

    public ItemsTabViewModel(IReferenceDataService refData, IReferenceNavigator navigator, IEntityNameResolver nameResolver, SilmarillionSettings? settings)
    {
        _refData = refData;
        _navigator = navigator;
        _nameResolver = nameResolver;
        // null → owned default instance keeps non-DI callers (tests) working without forcing
        // every fixture to construct one. The DI path always passes the live singleton.
        _settings = settings ?? new SilmarillionSettings();
        _openEntityCommand = new RelayCommand<EntityRef?>(r => { if (r is not null) _navigator.Open(r); });
        _allItems = BuildAllItems(refData);
        refData.FileUpdated += OnFileUpdated;
        _settings.PropertyChanged += OnSettingsChanged;
    }

    [ObservableProperty]
    private IReadOnlyList<Item> _allItems;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    private Item? _selectedItem;

    [ObservableProperty]
    private ItemDetailViewModel? _detailViewModel;

    partial void OnSelectedItemChanged(Item? value)
    {
        if (value is null)
        {
            DetailViewModel = null;
            return;
        }
        var context = BuildCrossLinkContext(value);
        DetailViewModel = new ItemDetailViewModel(
            value, _refData, context, poolPresenter: null, openEntityCommand: _openEntityCommand);
    }

    private void OnFileUpdated(object? sender, string fileKey)
    {
        // Items.json is the primary trigger. Recipes.json + quests.json updates also
        // rebuild refData's cross-link indices (BuildRecipeCrossLinkIndices /
        // BuildQuestCrossLinkIndices run on those), which means an open ItemDetailView's
        // "Produced by" / "Used in" / "Awarded by" chips can go stale even when items.json
        // itself didn't change. Re-resolve on all three.
        if (fileKey is not ("items" or "recipes" or "quests")) return;

        UiThread.Run(() =>
        {
            var captured = SelectedItem?.InternalName;
            // items.json refresh rebuilds the bound list. recipes.json refresh
            // only changes the cross-link side; AllItems doesn't need rebuilding.
            if (fileKey == "items")
            {
                AllItems = BuildAllItems(_refData);
            }
            if (!string.IsNullOrEmpty(captured))
            {
                var resolved = AllItems.FirstOrDefault(i => i.InternalName == captured);
                // Force a SelectedItem change so OnSelectedItemChanged rebuilds the
                // detail VM with the fresh refData snapshot. Toggling through null is
                // necessary because [ObservableProperty]'s equality check would
                // suppress a same-reference reassignment.
                SelectedItem = null;
                SelectedItem = resolved;
            }
        });
    }

    private static IReadOnlyList<Item> BuildAllItems(IReferenceDataService refData) =>
        refData.ItemsByInternalName.Values
            .OrderBy(i => i.Name ?? i.InternalName ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // UsedInChipCap controls the "Used in" overflow pill threshold; rebuild the live
        // detail VM when it changes so the slider feels immediate. Toggle through null to
        // sidestep [ObservableProperty]'s reference-equality check.
        if (e.PropertyName != nameof(SilmarillionSettings.UsedInChipCap)) return;
        var captured = SelectedItem;
        if (captured is null) return;
        UiThread.Run(() =>
        {
            SelectedItem = null;
            SelectedItem = captured;
        });
    }

    private ItemDetailContext BuildCrossLinkContext(Item item)
    {
        if (string.IsNullOrEmpty(item.InternalName))
        {
            return ItemDetailContext.Empty;
        }
        var (consumed, popup) = BuildConsumedBy(item);
        var (consumedAsKeyword, keywordPopup) = BuildConsumedAsKeyword(item);
        return new ItemDetailContext(
            ProducedByRecipes: BuildRecipeChips(_refData.RecipesByProducedItem, item.InternalName!),
            ConsumedByRecipes: consumed,
            ConsumedAsKeywordIn: consumedAsKeyword,
            AwardedByQuests: BuildAwardedByQuestChips(item.InternalName!),
            BestowsLorebook: BuildBestowsLorebookChip(item),
            Sources: BuildSourceChips(item.InternalName!),
            ConsumedByRecipesPopup: popup,
            ConsumedAsKeywordInPopup: keywordPopup);
    }

    /// <summary>
    /// Inbound 1:1 cross-link (#247): resolve <see cref="Item.BestowLoreBook"/> (an
    /// <c>int?</c> = numeric Book id) to the lorebook via <c>LorebooksById</c> and surface
    /// it as a single navigable chip. Null when the item bestows no book or the id doesn't
    /// resolve (defensive — a dangling id shouldn't render a dead chip).
    /// </summary>
    private EntityChipVm? BuildBestowsLorebookChip(Item item)
    {
        if (item.BestowLoreBook is not { } bookId) return null;
        if (!_refData.LorebooksById.TryGetValue(bookId, out var book)) return null;
        if (string.IsNullOrEmpty(book.InternalName)) return null;
        var reference = EntityRef.Lorebook(book.InternalName!);
        return new EntityChipVm(
            DisplayName: _nameResolver.Resolve(reference),
            IconId: 0,
            Reference: reference,
            IsNavigable: _navigator.CanOpen(reference));
    }

    private IReadOnlyList<EntityChipVm>? BuildAwardedByQuestChips(string itemInternalName)
    {
        if (!_refData.QuestsRewardingItem.TryGetValue(itemInternalName, out var quests) || quests.Count == 0)
            return null;
        return quests
            .OrderBy(q => q.Name ?? q.InternalName ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(q =>
            {
                var internalName = q.InternalName ?? "";
                var reference = EntityRef.Quest(internalName);
                return new EntityChipVm(
                    DisplayName: _nameResolver.Resolve(reference),
                    IconId: 0,
                    Reference: reference,
                    IsNavigable: _navigator.CanOpen(reference));
            })
            .ToList();
    }

    /// <summary>
    /// Build the item-detail "Used in" surface (#318 slice 4, surface 1) from
    /// <see cref="IReferenceDataService.RecipesByIngredientItemWithReason"/> <b>directly</b>:
    /// the capped chip cluster (first <see cref="SilmarillionSettings.UsedInChipCap"/> by
    /// name) <em>and</em> the provenance popup VM. Both are projected from the <em>same</em>
    /// materialized index collection — there is no query re-derivation, so the popup's
    /// "View all N" count and membership cannot diverge from the index (the #318
    /// invariant). Replaces the retired <c>RecipeIngredientItem</c> synthetic-kind
    /// ActionChip deep link.
    /// <para>
    /// The relationship is <b>single-reason</b>: a recipe qualifies only via a direct
    /// <see cref="RecipeItemIngredient"/> (keyword slots are a separate surface), so the
    /// index carries one <see cref="RecipeIngredientItemMatchReason.DirectIngredient"/>
    /// flag per member and the popup is built with a single section —
    /// <see cref="ProvenancePopupViewModel"/> collapses that to a flat list (a single
    /// trivial reason is noise, #318 Discipline). Returns <c>(null, null)</c> only when no
    /// recipe consumes the item.
    /// </para>
    /// </summary>
    private (IReadOnlyList<EntityChipVm>? Chips, ProvenancePopupViewModel? Popup) BuildConsumedBy(Item item)
    {
        var itemName = item.InternalName!;
        if (!_refData.RecipesByIngredientItemWithReason.TryGetValue(itemName, out var matches)
            || matches.Count == 0)
        {
            return (null, null);
        }

        // Single materialization: order the index members once; both the popup and the
        // capped cluster are views over this exact list.
        var ordered = matches
            .OrderBy(m => m.Recipe.Name ?? m.Recipe.InternalName ?? m.Recipe.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        EntityChipVm Chip(Recipe r)
        {
            var reference = EntityRef.Recipe(r.InternalName ?? r.Key);
            return new EntityChipVm(
                DisplayName: r.Name ?? r.InternalName ?? r.Key,
                IconId: r.IconId > 0 ? r.IconId : ResolveRecipeFallbackIcon(r),
                Reference: reference,
                IsNavigable: _navigator.CanOpen(reference));
        }

        var allChips = ordered.Select(m => Chip(m.Recipe)).ToList();

        var cap = _settings.UsedInChipCap;
        var capped = cap == 0
            ? (IReadOnlyList<EntityChipVm>)Array.Empty<EntityChipVm>()
            : allChips.Take(cap).ToList();

        // Single-reason ⇒ exactly one section ⇒ ProvenancePopupViewModel renders a flat
        // list (no reason header). ToQueryCommand intentionally unset (mirrors the slice-2
        // effect→abilities decision): the popup-from-index is the count-bearing surface;
        // the labeled-lossy To-Query projection is a deliberate fast-follow.
        var popup = new ProvenancePopupViewModel(
            title: $"Recipes that use {item.Name ?? itemName}",
            sections: new List<ProvenancePopupSection>
            {
                new("Used in", allChips),
            });

        return (capped, popup);
    }

    /// <summary>
    /// Build the item-detail "Used as" surface (#318 slice 4, surface 2 —
    /// <c>RecipeIngredientKeyword</c> #259) from
    /// <see cref="IReferenceDataService.RecipesByIngredientKeywordWithReason"/>
    /// <b>directly</b>: the capped recipe-chip cluster (first
    /// <see cref="SilmarillionSettings.UsedInChipCap"/> by name) <em>and</em> the
    /// provenance popup VM. The 1:N relationship is "recipes that consume <em>this item</em>
    /// via a keyword-ingredient slot" — the union, over every keyword tag the item carries
    /// that is used in a recipe slot, of the recipes matched for that tag. A recipe that
    /// matches via several of the item's tags is carried <b>once</b> (deduped by recipe),
    /// so a distinct-member count equals the displayed "View all N". Both the cluster and
    /// the popup are projected from the <em>same</em> materialized member list — there is
    /// no query re-derivation, so the popup's count and membership cannot diverge from the
    /// index (the #318 invariant). Replaces the retired per-keyword
    /// <c>RecipeIngredientKeyword</c> synthetic-kind ActionChips that each deep-linked to
    /// <c>IngredientKeywords CONTAINS "&lt;tag&gt;"</c>.
    /// <para>
    /// The relationship is <b>single-reason</b>: a recipe qualifies only via a
    /// <see cref="RecipeKeywordIngredient"/> slot (the sole structural mechanic; which of
    /// the item's tags matched is data, not a structural reason — the analogue of
    /// surface 1's single <c>DirectIngredient</c> decision), so the index carries one
    /// <see cref="RecipeIngredientKeywordMatchReason.KeywordIngredientSlot"/> flag per
    /// member and the popup is built with a single section —
    /// <see cref="ProvenancePopupViewModel"/> collapses that to a flat list (a single
    /// trivial reason is noise, #318 Discipline). Returns <c>(null, null)</c> only when no
    /// recipe consumes the item via any of its keyword tags.
    /// </para>
    /// </summary>
    private (IReadOnlyList<EntityChipVm>? Chips, ProvenancePopupViewModel? Popup) BuildConsumedAsKeyword(Item item)
    {
        if (item.Keywords is null || item.Keywords.Count == 0)
            return (null, null);
        var index = _refData.RecipesByIngredientKeywordWithReason;
        if (index.Count == 0) return (null, null);

        // Single materialization: union the per-tag index members for every keyword tag
        // the item carries, deduping by recipe (a recipe matching via several of the
        // item's tags is one member). Dedup on the recipe's stable identity. Both the
        // popup and the capped cluster are views over this exact ordered list — no second
        // derivation.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<Recipe>();
        foreach (var kw in item.Keywords)
        {
            if (!index.TryGetValue(kw.Tag, out var matches)) continue;
            foreach (var m in matches)
            {
                var id = m.Recipe.InternalName ?? m.Recipe.Key;
                if (seen.Add(id))
                    members.Add(m.Recipe);
            }
        }
        if (members.Count == 0) return (null, null);

        var ordered = members
            .OrderBy(r => r.Name ?? r.InternalName ?? r.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        EntityChipVm Chip(Recipe r)
        {
            var reference = EntityRef.Recipe(r.InternalName ?? r.Key);
            return new EntityChipVm(
                DisplayName: r.Name ?? r.InternalName ?? r.Key,
                IconId: r.IconId > 0 ? r.IconId : ResolveRecipeFallbackIcon(r),
                Reference: reference,
                IsNavigable: _navigator.CanOpen(reference));
        }

        var allChips = ordered.Select(Chip).ToList();

        var cap = _settings.UsedInChipCap;
        var capped = cap == 0
            ? (IReadOnlyList<EntityChipVm>)Array.Empty<EntityChipVm>()
            : allChips.Take(cap).ToList();

        // Single-reason ⇒ exactly one section ⇒ ProvenancePopupViewModel renders a flat
        // list (no reason header). ToQueryCommand intentionally unset (mirrors surface 1 /
        // the slice-2 effect→abilities decision): the popup-from-index is the
        // count-bearing surface; the labeled-lossy To-Query projection is a deliberate
        // fast-follow.
        var popup = new ProvenancePopupViewModel(
            title: $"Recipes that use {item.Name ?? item.InternalName} as an ingredient keyword",
            sections: new List<ProvenancePopupSection>
            {
                new("Used as", allChips),
            });

        return (capped, popup);
    }

    private IReadOnlyList<EntityChipVm>? BuildRecipeChips(
        IReadOnlyDictionary<string, IReadOnlyList<Recipe>> index,
        string itemInternalName)
    {
        if (!index.TryGetValue(itemInternalName, out var recipes) || recipes.Count == 0)
        {
            return null;
        }
        return recipes
            .OrderBy(r => r.Name ?? r.InternalName ?? r.Key, StringComparer.OrdinalIgnoreCase)
            .Select(r => new EntityChipVm(
                DisplayName: r.Name ?? r.InternalName ?? r.Key,
                IconId: r.IconId > 0 ? r.IconId : ResolveRecipeFallbackIcon(r),
                Reference: EntityRef.Recipe(r.InternalName ?? r.Key),
                IsNavigable: _navigator.CanOpen(EntityRef.Recipe(r.InternalName ?? r.Key))))
            .ToList();
    }

    /// <summary>
    /// Many recipes in the bundled JSON ship with IconId = 0; their visual identity is
    /// carried by the result item's icon. Walk ResultItems → ProtoResultItems → 0 for
    /// the chip-icon fallback so cross-link chips have something to render.
    /// </summary>
    private int ResolveRecipeFallbackIcon(Mithril.Reference.Models.Recipes.Recipe recipe)
    {
        var source = (recipe.ResultItems is { Count: > 0 } ? recipe.ResultItems : recipe.ProtoResultItems)
            ?? (IReadOnlyList<Mithril.Reference.Models.Recipes.RecipeResultItem>)Array.Empty<Mithril.Reference.Models.Recipes.RecipeResultItem>();
        foreach (var result in source)
        {
            if (_refData.Items.TryGetValue(result.ItemCode, out var item) && item.IconId > 0)
                return item.IconId;
        }
        return 0;
    }

    private IReadOnlyList<ItemSourceChipVm>? BuildSourceChips(string itemInternalName)
    {
        if (!_refData.ItemSources.TryGetValue(itemInternalName, out var sources) || sources.Count == 0)
        {
            return null;
        }
        // NPC-anchored source kinds (Vendor / Barter / NpcGift / HangOut / Training) carry an
        // <see cref="EntityRef.Npc"/> — navigable since #241 shipped the NPCs kind target.
        // Quest sources resolve s.Context to the quest InternalName via the parser's
        // ResolveSourceContext; surface them as EntityRef.Quest chips so the moment the Quests
        // kind target is registered (#242 — this PR), every "Quest reward" chip across the
        // codebase becomes navigable without further changes. Other source kinds (Monster /
        // Recipe / Skill / …) leave the reference null and render as plain text.
        return sources
            .Select(s =>
            {
                var reference = ResolveSourceReference(s);
                return new ItemSourceChipVm(
                    DisplayName: FormatSourceDisplayName(s),
                    Detail: s.Context,
                    IconId: null,
                    EntityReference: reference,
                    IsNavigable: reference is not null && _navigator.CanOpen(reference));
            })
            .ToList();
    }

    private static EntityRef? ResolveSourceReference(ItemSource s)
    {
        if (!string.IsNullOrEmpty(s.Npc)) return EntityRef.Npc(s.Npc!);
        if (string.Equals(s.Type, "Quest", StringComparison.Ordinal) && !string.IsNullOrEmpty(s.Context))
            return EntityRef.Quest(s.Context!);
        if (string.Equals(s.Type, "Recipe", StringComparison.Ordinal) && !string.IsNullOrEmpty(s.Context))
            return EntityRef.Recipe(s.Context!);
        return null;
    }

    private string FormatSourceDisplayName(ItemSource s)
    {
        if (!string.IsNullOrEmpty(s.Npc))
            return $"{s.Type}: {_nameResolver.Resolve(EntityRef.Npc(s.Npc!))}";
        if (string.Equals(s.Type, "Quest", StringComparison.Ordinal) && !string.IsNullOrEmpty(s.Context))
            return $"Quest: {_nameResolver.Resolve(EntityRef.Quest(s.Context!))}";
        if (string.Equals(s.Type, "Recipe", StringComparison.Ordinal) && !string.IsNullOrEmpty(s.Context))
            return $"Recipe: {_nameResolver.Resolve(EntityRef.Recipe(s.Context!))}";
        return s.Type;
    }
}
