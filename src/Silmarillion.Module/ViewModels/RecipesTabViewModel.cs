using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.Query;

namespace Silmarillion.ViewModels;

/// <summary>
/// Recipes master-detail view-model. Mirrors <see cref="ItemsTabViewModel"/>'s shape:
/// filterable row list on the left, recipe detail on the right. On selection change
/// builds a <see cref="RecipeDetailViewModel"/> with ingredient/produced chips
/// resolved from <c>IReferenceDataService</c>.
///
/// Subscribes to <see cref="IReferenceDataService.FileUpdated"/> for <c>"recipes"</c>
/// and <c>"items"</c> (the latter rebuilds chip resolution) and rebuilds
/// <see cref="AllRecipes"/> on the UI thread, preserving the current selection by
/// <see cref="Recipe.InternalName"/>.
/// </summary>
public sealed partial class RecipesTabViewModel : ObservableObject, ITabViewModel
{
    /// <summary>
    /// Reflected schema for <see cref="RecipeListRow"/> exposed to <c>MithrilQueryBox.Schema</c>
    /// so the query box can offer completion and highlight known column names. <c>QueryFilter</c>
    /// on the bound ListBox reflects the same surface from the item type at attach time, so the
    /// suggestions stay in sync with what actually filters.
    /// </summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(RecipeListRow)));

    public string TabHeader => "Recipes";
    public int TabOrder => 1;

    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;
    private readonly IEntityNameResolver _nameResolver;
    private readonly RelayCommand<EntityRef?> _openEntityCommand;

    public RecipesTabViewModel(IReferenceDataService refData, IReferenceNavigator navigator, IEntityNameResolver nameResolver)
    {
        _refData = refData;
        _navigator = navigator;
        _nameResolver = nameResolver;
        _openEntityCommand = new RelayCommand<EntityRef?>(r => { if (r is not null) _navigator.Open(r); });
        _allRecipes = BuildAllRecipes(refData);
        refData.FileUpdated += OnFileUpdated;
    }

    [ObservableProperty]
    private IReadOnlyList<RecipeListRow> _allRecipes;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    /// <summary>
    /// ListBox-bound selection. Setting it from a Recipe POCO (in tests or via the navigator)
    /// resolves to the matching row.
    /// </summary>
    [ObservableProperty]
    private RecipeListRow? _selectedRow;

    /// <summary>
    /// Convenience accessor — the actual <see cref="Recipe"/> behind the selected row.
    /// Setter resolves the recipe to its row in <see cref="AllRecipes"/> by InternalName
    /// (not reference equality, since refresh swaps can hand out a different instance for
    /// the same name).
    /// </summary>
    public Recipe? SelectedRecipe
    {
        get => SelectedRow?.Recipe;
        set => SelectedRow = value is null
            ? null
            : AllRecipes.FirstOrDefault(row => row.Recipe.InternalName == value.InternalName);
    }

    [ObservableProperty]
    private RecipeDetailViewModel? _detailViewModel;

    partial void OnSelectedRowChanged(RecipeListRow? value)
    {
        OnPropertyChanged(nameof(SelectedRecipe));
        if (value is null)
        {
            DetailViewModel = null;
            return;
        }

        var recipe = value.Recipe;
        var ingredients = BuildIngredientChips(recipe);
        var produced = BuildProducedChips(recipe);
        var effects = recipe.ResultEffects ?? Array.Empty<string>();
        var sources = BuildSourceChips(recipe);
        var keywordSlots = BuildKeywordSlots(recipe);
        DetailViewModel = new RecipeDetailViewModel(
            recipe, ingredients, produced, effects, _openEntityCommand, value.SkillDisplayName, sources, keywordSlots);
    }

    private void OnFileUpdated(object? sender, string fileKey)
    {
        // recipes.json updates rebuild the bound list. items.json updates only refresh
        // the ingredient/produced chip resolution; AllRecipes doesn't change.
        if (fileKey != "recipes" && fileKey != "items") return;

        UiThread.Run(() =>
        {
            var captured = SelectedRow?.Recipe.InternalName;
            if (fileKey == "recipes")
            {
                AllRecipes = BuildAllRecipes(_refData);
            }
            if (!string.IsNullOrEmpty(captured))
            {
                var resolved = AllRecipes.FirstOrDefault(r => r.Recipe.InternalName == captured);
                // Toggle through null to force OnSelectedRowChanged to rebuild the detail
                // VM with the fresh refData snapshot (chip resolution uses live refData).
                SelectedRow = null;
                SelectedRow = resolved;
            }
        });
    }

    private IReadOnlyList<RecipeListRow> BuildAllRecipes(IReferenceDataService refData) =>
        refData.Recipes.Values
            .Select(r => new RecipeListRow(
                Recipe: r,
                Name: r.Name ?? r.InternalName ?? r.Key,
                IconId: r.IconId > 0 ? r.IconId : ResolveResultIcon(r),
                SkillDisplayName: ResolveSkillDisplayName(r.Skill),
                SkillLevelReq: r.SkillLevelReq,
                IngredientKeywords: BuildIngredientKeywords(r),
                Ingredients: BuildIngredientItems(r, refData)))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<IngredientKeywordValue> BuildIngredientKeywords(Recipe recipe)
    {
        if (recipe.Ingredients is null) return [];
        return recipe.Ingredients
            .OfType<RecipeKeywordIngredient>()
            .SelectMany(slot => slot.ItemKeys)
            .Distinct(StringComparer.Ordinal)
            .Select(tag => new IngredientKeywordValue(tag))
            .ToList();
    }

    private static IReadOnlyList<IngredientItemValue> BuildIngredientItems(Recipe recipe, IReferenceDataService refData)
    {
        if (recipe.Ingredients is null) return [];
        return recipe.Ingredients
            .OfType<RecipeItemIngredient>()
            .Select(slot => refData.Items.TryGetValue(slot.ItemCode, out var item) ? item.InternalName : null)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .Select(name => new IngredientItemValue(name))
            .ToList();
    }

    /// <summary>
    /// Convenience accessor — the actual <see cref="Recipe"/> behind the selected row.
    /// Setter resolves the recipe to its row in <see cref="AllRecipes"/>. Tests and the
    /// navigator's OnNavigated handler write through this property.
    /// </summary>
    private string? ResolveSkillDisplayName(string? skillKey) =>
        !string.IsNullOrEmpty(skillKey) && _refData.Skills.TryGetValue(skillKey, out var s)
            ? s.DisplayName
            : skillKey;

    private int ResolveResultIcon(Recipe recipe)
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

    private IReadOnlyList<EntityChipVm> BuildIngredientChips(Recipe recipe) =>
        (recipe.Ingredients ?? (IReadOnlyList<RecipeIngredient>)Array.Empty<RecipeIngredient>())
            .Select(BuildIngredientChip)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

    // Item-ingredient chips only. Keyword slots are a 1:N fan-out (one slot → N matching
    // items): per the #318 chip-vs-popup rule (slice 4, surface 3) they no longer emit a
    // synthetic-kind EntityRef.ItemKeyword chip here — they surface via the recipe-detail
    // "Keyword ingredients" section's provenance popup (BuildKeywordSlots, below).
    private EntityChipVm? BuildIngredientChip(RecipeIngredient ingredient) => ingredient switch
    {
        RecipeItemIngredient itemIng => BuildItemChip(itemIng.ItemCode, itemIng.StackSize, percentChance: null),
        _ => null,
    };

    /// <summary>
    /// Build the recipe-detail keyword-slot rows (#318 slice 4, surface 3 — retiring the
    /// synthetic <c>ItemKeyword</c> #270 deep link). Each <see cref="RecipeKeywordIngredient"/>
    /// slot is a 1:N fan-out; per the #318 chip-vs-popup rule it surfaces as a provenance
    /// popup fed <see cref="IReferenceDataService.ItemsByRecipeKeywordSlotWithReason"/>
    /// <b>directly</b> — there is no query re-derivation, so the popup's "View all N" count
    /// and membership cannot diverge from the index (the #318 invariant; this is exactly
    /// the divergence the old <c>ItemKeywordQueryMapper</c> suffered — it failed whole-slot
    /// on prereq keys the keyword index nonetheless matched). The relationship is
    /// single-reason (<see cref="RecipeKeywordItemMatchReason.KeywordMatch"/>) so each
    /// popup is a single section — <see cref="ProvenancePopupViewModel"/> collapses that to
    /// a flat list (a single trivial reason is noise, #318 Discipline). Slots whose keys
    /// aren't in the index (the data shifted between item/recipe loads) are skipped rather
    /// than rendering a dead row.
    /// </summary>
    private IReadOnlyList<RecipeKeywordSlotVm> BuildKeywordSlots(Recipe recipe)
    {
        var ingredients = recipe.Ingredients;
        if (ingredients is null) return [];

        var rows = new List<RecipeKeywordSlotVm>();
        foreach (var kwIng in ingredients.OfType<RecipeKeywordIngredient>())
        {
            if (kwIng.ItemKeys.Count == 0) continue;
            var slotKey = string.Join('+', kwIng.ItemKeys);
            if (!_refData.ItemsByRecipeKeywordSlotWithReason.TryGetValue(slotKey, out var matches))
                continue;

            // Single materialization: order the index members once; the popup is a view
            // over this exact list.
            var ordered = matches
                .OrderBy(m => m.Item.Name ?? m.Item.InternalName ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();

            EntityChipVm Chip(Mithril.Reference.Models.Items.Item item)
            {
                var reference = EntityRef.Item(item.InternalName ?? "");
                return new EntityChipVm(
                    DisplayName: item.Name ?? item.InternalName ?? "",
                    IconId: item.IconId,
                    Reference: reference,
                    IsNavigable: _navigator.CanOpen(reference));
            }

            var chips = ordered.Select(m => Chip(m.Item)).ToList();
            var label = kwIng.Desc ?? $"any {ItemKeywordIndex.Humanise(kwIng.ItemKeys)}";

            // Single-reason ⇒ exactly one section ⇒ ProvenancePopupViewModel renders a
            // flat list (no reason header). ToQueryCommand intentionally unset (mirrors
            // the surface-1 decision): the popup-from-index is the count-bearing surface.
            var popup = new ProvenancePopupViewModel(
                title: $"Items matching {label}",
                sections: new List<ProvenancePopupSection>
                {
                    new(label, chips),
                });

            rows.Add(new RecipeKeywordSlotVm(label, popup, _openEntityCommand));
        }
        return rows;
    }

    private IReadOnlyList<EntityChipVm> BuildProducedChips(Recipe recipe)
    {
        var source = (recipe.ResultItems is { Count: > 0 } ? recipe.ResultItems : recipe.ProtoResultItems)
            ?? (IReadOnlyList<RecipeResultItem>)Array.Empty<RecipeResultItem>();
        return source
            .Select(res => BuildItemChip(res.ItemCode, res.StackSize, res.PercentChance))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();
    }

    private EntityChipVm? BuildItemChip(long itemCode, int stackSize, double? percentChance)
    {
        if (!_refData.Items.TryGetValue(itemCode, out var item) || string.IsNullOrEmpty(item.InternalName))
            return null;
        var displayName = stackSize > 1
            ? $"{item.Name ?? item.InternalName} ×{stackSize}"
            : item.Name ?? item.InternalName ?? "";
        if (percentChance is { } pc && pc < 100)
        {
            displayName += $" ({pc:0}%)";
        }
        var reference = EntityRef.Item(item.InternalName!);
        return new EntityChipVm(displayName, item.IconId, reference, _navigator.CanOpen(reference));
    }

    /// <summary>
    /// Project the recipe's <c>sources_recipes.json</c> entries to display chips. NPC chips
    /// carry an <see cref="EntityRef.Npc"/> reference so they'll become clickable the moment
    /// an NPCs tab ships (#241); other source kinds (Skill, Effect, Quest, …) render as plain
    /// text with the <c>Context</c> resolved by <see cref="ReferenceDataService.ResolveSourceContext"/>.
    /// Returns null when no sources are recorded — drives the empty-section hide in XAML.
    /// </summary>
    private IReadOnlyList<ItemSourceChipVm>? BuildSourceChips(Recipe recipe)
    {
        if (string.IsNullOrEmpty(recipe.InternalName)) return null;
        if (!_refData.RecipeSources.TryGetValue(recipe.InternalName!, out var sources) || sources.Count == 0)
            return null;

        // Mirror of ItemsTabViewModel.BuildSourceChips — see that file for the rationale
        // on Quest-context navigability. Recipe-as-quest-reward is the common case here
        // (a quest teaches you a recipe), so the Quest chip flip is load-bearing for this
        // tab.
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

    private static EntityRef? ResolveSourceReference(RecipeSource s)
    {
        if (!string.IsNullOrEmpty(s.Npc)) return EntityRef.Npc(s.Npc!);
        if (string.Equals(s.Type, "Quest", StringComparison.Ordinal) && !string.IsNullOrEmpty(s.Context))
            return EntityRef.Quest(s.Context!);
        return null;
    }

    private string FormatSourceDisplayName(RecipeSource s)
    {
        if (!string.IsNullOrEmpty(s.Npc))
            return $"{s.Type}: {_nameResolver.Resolve(EntityRef.Npc(s.Npc!))}";
        if (string.Equals(s.Type, "Quest", StringComparison.Ordinal) && !string.IsNullOrEmpty(s.Context))
            return $"Quest: {_nameResolver.Resolve(EntityRef.Quest(s.Context!))}";
        return s.Type;
    }
}
