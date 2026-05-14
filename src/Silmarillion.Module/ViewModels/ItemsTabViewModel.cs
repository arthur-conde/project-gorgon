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
public sealed partial class ItemsTabViewModel : ObservableObject
{
    /// <summary>
    /// Reflected schema for <see cref="Item"/> exposed to <c>MithrilQueryBox.Schema</c> so the
    /// query box can offer completion and highlight known column names. <c>QueryFilter</c> on
    /// the bound ListBox reflects the same surface from the item type at attach time, so the
    /// suggestions stay in sync with what actually filters.
    /// </summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(Item)));

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
        // Items.json is the primary trigger. Recipes.json updates also rebuild
        // refData's cross-link indices (BuildRecipeCrossLinkIndices runs on both),
        // which means an open ItemDetailView's "Produced by" / "Used in" chips can
        // go stale even when items.json itself didn't change. Re-resolve on both.
        if (fileKey != "items" && fileKey != "recipes") return;

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
        var (consumed, more) = BuildConsumedByChips(_refData.RecipesByIngredientItem, item);
        return new ItemDetailContext(
            ProducedByRecipes: BuildRecipeChips(_refData.RecipesByProducedItem, item.InternalName!),
            ConsumedByRecipes: consumed,
            ConsumedAsKeywordIn: BuildKeywordChips(item),
            Sources: BuildSourceChips(item.InternalName!),
            MoreRecipesChip: more);
    }

    /// <summary>
    /// Apply <see cref="SilmarillionSettings.UsedInChipCap"/> to the "Used in" chip list.
    /// When the underlying recipe count exceeds the cap, returns the first N chips plus a
    /// pill chip pointing to the symmetric recipe-tab filter
    /// (<c>Ingredients CONTAINS "&lt;itemInternalName&gt;"</c>). Pill carries the item's own
    /// icon for visual continuity with the detail header.
    /// </summary>
    private (IReadOnlyList<EntityChipVm>? Chips, EntityChipVm? More) BuildConsumedByChips(
        IReadOnlyDictionary<string, IReadOnlyList<Recipe>> index,
        Item item)
    {
        var itemName = item.InternalName!;
        var all = BuildRecipeChips(index, itemName);
        if (all is null) return (null, null);

        var cap = _settings.UsedInChipCap;
        if (all.Count <= cap) return (all, null);

        var capped = cap == 0 ? (IReadOnlyList<EntityChipVm>)Array.Empty<EntityChipVm>() : all.Take(cap).ToList();
        var overflow = all.Count - cap;
        var reference = EntityRef.RecipeIngredientItem(itemName);
        var pill = new EntityChipVm(
            DisplayName: $"+{overflow} more →",
            IconId: item.IconId,
            Reference: reference,
            IsNavigable: _navigator.CanOpen(reference));
        return (capped, pill);
    }

    private IReadOnlyList<EntityChipVm>? BuildKeywordChips(Item item)
    {
        if (item.Keywords is null || item.Keywords.Count == 0)
            return null;
        var used = _refData.KeywordsUsedInRecipeSlots;
        if (used.Count == 0) return null;
        var displayNames = _refData.KeywordDisplayNames;
        var chips = item.Keywords
            .Where(k => used.Contains(k.Tag))
            .Select(k =>
            {
                var reference = EntityRef.RecipeIngredientKeyword(k.Tag);
                var display = displayNames.TryGetValue(k.Tag, out var friendly)
                    ? friendly
                    : CamelCaseSplitConverter.Split(k.Tag);
                return new EntityChipVm(
                    DisplayName: display,
                    IconId: 0,
                    Reference: reference,
                    IsNavigable: _navigator.CanOpen(reference));
            })
            .ToList();
        return chips.Count == 0 ? null : chips;
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
        // <see cref="EntityRef.Npc"/> so they're navigable the moment the NPCs kind target ships
        // (#241). Other source kinds (Monster drop, Recipe, Quest, Skill, …) leave the reference
        // null and render as plain-text in the source chip. Mirrors RecipesTabViewModel.BuildSourceChips.
        return sources
            .Select(s =>
            {
                var reference = string.IsNullOrEmpty(s.Npc) ? null : EntityRef.Npc(s.Npc!);
                return new ItemSourceChipVm(
                    DisplayName: FormatSourceDisplayName(s),
                    Detail: s.Context,
                    IconId: null,
                    EntityReference: reference,
                    IsNavigable: reference is not null && _navigator.CanOpen(reference));
            })
            .ToList();
    }

    private string FormatSourceDisplayName(ItemSource s) =>
        string.IsNullOrEmpty(s.Npc)
            ? s.Type
            : $"{s.Type}: {_nameResolver.Resolve(EntityRef.Npc(s.Npc!))}";
}
