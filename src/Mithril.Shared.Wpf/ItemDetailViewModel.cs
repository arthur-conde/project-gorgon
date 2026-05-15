using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Mithril.Reference.Models.Items;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Read-only projection of an <see cref="Item"/> for <see cref="ItemDetailWindow"/>.
/// Item data is immutable within a window instance — open a new window to inspect a
/// different item rather than mutating this view-model.
/// </summary>
public sealed partial class ItemDetailViewModel
{
    private readonly IAugmentPoolPresenter? _poolPresenter;

    /// <summary>
    /// Host-supplied opener for the "Used in" (consumed-by-recipes) provenance popup
    /// (#318 slice 4, surface 1). Defaults to <see cref="ShowProvenancePopupWindow"/>
    /// (creates + <c>Show()</c>s a <see cref="ProvenancePopupWindow"/>). Tests swap in a
    /// capturing delegate so the VM is fully assertable without spawning a window. Opening
    /// the popup this way never calls <c>IReferenceNavigator</c>, so it pushes no
    /// back/forward history — identical non-navigating contract to
    /// <c>IReferenceKindTarget.TryOpenInWindow</c> and to
    /// <c>EffectDetailViewModel.ProvenancePopupOpener</c> (the slice-2 reference).
    /// </summary>
    public static Action<ProvenancePopupViewModel, ICommand?> ProvenancePopupOpener { get; set; }
        = ShowProvenancePopupWindow;

    private static void ShowProvenancePopupWindow(ProvenancePopupViewModel vm, ICommand? chipClick) =>
        new ProvenancePopupWindow { DataContext = vm, ChipClickCommand = chipClick }.Show();

    public ItemDetailViewModel(Item item, IReferenceDataService refData)
        : this(item, refData, ItemDetailContext.Empty, poolPresenter: null)
    {
    }

    public ItemDetailViewModel(Item item, IReferenceDataService refData, IReadOnlyList<AugmentPreview>? augments)
        : this(item, refData, new ItemDetailContext(Augments: augments), poolPresenter: null)
    {
    }

    public ItemDetailViewModel(
        Item item,
        IReferenceDataService refData,
        ItemDetailContext context,
        IAugmentPoolPresenter? poolPresenter = null,
        ICommand? openEntityCommand = null)
    {
        OpenEntityCommand = openEntityCommand;
        Item = item;
        EffectLines = EffectDescsRenderer.Render(item.EffectDescs, refData.Attributes);
        SkillReqChips = item.SkillReqs is null
            ? []
            : item.SkillReqs
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{ResolveSkillDisplayName(refData, kv.Key)} {kv.Value}")
                .ToList();
        Augments = context.Augments ?? [];
        WaxItems = context.WaxItems ?? [];
        WaxAugments = context.WaxAugments ?? [];
        AugmentPools = context.AugmentPools ?? [];
        TaughtRecipes = context.TaughtRecipes ?? [];
        EffectTags = context.EffectTags ?? [];
        ResearchProgress = context.ResearchProgress ?? [];
        XpGrants = context.XpGrants ?? [];
        WordsOfPower = context.WordsOfPower ?? [];
        LearnedAbilities = context.LearnedAbilities ?? [];
        ProducedItems = context.ProducedItems ?? [];
        EquipBonuses = context.EquipBonuses ?? [];
        CraftingEnhancements = context.CraftingEnhancements ?? [];
        RecipeCooldowns = context.RecipeCooldowns ?? [];
        UnpreviewableExtractions = context.UnpreviewableExtractions ?? [];
        ProducedByRecipes = context.ProducedByRecipes ?? [];
        ConsumedByRecipes = context.ConsumedByRecipes ?? [];
        ConsumedByRecipesPopup = context.ConsumedByRecipesPopup;
        ConsumedByRecipesTotal = context.ConsumedByRecipesPopup?.TotalCount ?? 0;
        ShowConsumedByRecipesPopupCommand = new RelayCommand(
            () => ProvenancePopupOpener(ConsumedByRecipesPopup!, OpenEntityCommand),
            () => ConsumedByRecipesPopup is not null);
        ConsumedAsKeywordIn = context.ConsumedAsKeywordIn ?? [];
        AwardedByQuests = context.AwardedByQuests ?? [];
        BestowsLorebook = context.BestowsLorebook;
        Sources = context.Sources ?? [];
        _poolPresenter = poolPresenter;
    }

    public Item Item { get; }
    public string DisplayName => Item.Name ?? "";
    public string InternalName => Item.InternalName ?? "";
    public int IconId => Item.IconId;
    public string? EquipSlot => Item.EquipSlot;
    public string? Description => Item.Description;
    public string? FoodDesc => Item.FoodDesc;
    public IReadOnlyList<string> SkillReqChips { get; }
    public IReadOnlyList<EffectLine> EffectLines { get; }
    public IReadOnlyList<AugmentPreview> Augments { get; }
    public IReadOnlyList<WaxItemPreview> WaxItems { get; }
    public IReadOnlyList<WaxAugmentPreview> WaxAugments { get; }
    public IReadOnlyList<AugmentPoolPreview> AugmentPools { get; }
    public IReadOnlyList<TaughtRecipePreview> TaughtRecipes { get; }
    public IReadOnlyList<EffectTagPreview> EffectTags { get; }
    public IReadOnlyList<ResearchProgressPreview> ResearchProgress { get; }
    public IReadOnlyList<XpGrantPreview> XpGrants { get; }
    public IReadOnlyList<WordOfPowerPreview> WordsOfPower { get; }
    public IReadOnlyList<LearnedAbilityPreview> LearnedAbilities { get; }
    public IReadOnlyList<ItemProducingPreview> ProducedItems { get; }
    public IReadOnlyList<EquipBonusPreview> EquipBonuses { get; }
    public IReadOnlyList<CraftingEnhancePreview> CraftingEnhancements { get; }
    public IReadOnlyList<RecipeCooldownPreview> RecipeCooldowns { get; }
    public IReadOnlyList<UnpreviewableExtractionPreview> UnpreviewableExtractions { get; }

    /// <summary>
    /// Recipes whose result includes this item. Populated by the Silmarillion master-detail
    /// flow; legacy callers (Celebrimbor pop-up window, deep links) pass null and the section
    /// stays hidden. Renders as plain text in Phase 1 — EntityChip control arrives in Phase 5.
    /// </summary>
    public IReadOnlyList<EntityChipVm> ProducedByRecipes { get; }

    /// <summary>
    /// Recipes that consume this item as an ingredient. Same lifecycle as
    /// <see cref="ProducedByRecipes"/>. Capped at
    /// <c>SilmarillionSettings.UsedInChipCap</c>; the full set is always reachable via
    /// the "View all N →" affordance that opens <see cref="ConsumedByRecipesPopup"/>.
    /// </summary>
    public IReadOnlyList<EntityChipVm> ConsumedByRecipes { get; }

    /// <summary>
    /// The "Used in" provenance popup VM opened by
    /// <see cref="ShowConsumedByRecipesPopupCommand"/>, or <see langword="null"/> when no
    /// recipe consumes this item (#318 slice 4, surface 1). Built from
    /// <c>IReferenceDataService.RecipesByIngredientItemWithReason</c> directly (membership
    /// + provenance), replacing the retired <c>RecipeIngredientItem</c> synthetic-kind
    /// deep link — there is no query re-derivation, so the displayed set cannot diverge
    /// from the index. The relationship is single-reason (<c>DirectIngredient</c>) so the
    /// popup collapses to a flat list (#318 Discipline).
    /// </summary>
    public ProvenancePopupViewModel? ConsumedByRecipesPopup { get; }

    /// <summary>
    /// Distinct count of recipes that consume this item as a direct ingredient — equals
    /// <see cref="ProvenancePopupViewModel.TotalCount"/> of
    /// <see cref="ConsumedByRecipesPopup"/>. Drives the "View all N →" label. 0 ⇒ no
    /// relationship and the whole "Used in" section hides.
    /// </summary>
    public int ConsumedByRecipesTotal { get; }

    /// <summary>
    /// Opens <see cref="ConsumedByRecipesPopup"/> via <see cref="ProvenancePopupOpener"/>.
    /// Bound to the always-visible "View all N →" affordance. The popup is a window shown
    /// directly — opening it pushes no navigator history (#229 contract; mirrors the
    /// slice-2 <c>EffectDetailViewModel.ShowRequiredByAbilitiesPopupCommand</c>).
    /// </summary>
    public ICommand ShowConsumedByRecipesPopupCommand { get; }

    /// <summary>
    /// Per-keyword chips for the item-detail "Used as" section. One chip per keyword the
    /// item carries that also appears in some recipe's keyword-slot tuple. Clicking deep-links
    /// to the Recipes tab pre-filtered via QueryText = IngredientKeywords CONTAINS "&lt;keyword&gt;".
    /// </summary>
    public IReadOnlyList<EntityChipVm> ConsumedAsKeywordIn { get; }

    /// <summary>
    /// Quests that include this item in <c>Rewards_Items</c>. Populated from
    /// <c>IReferenceDataService.QuestsRewardingItem</c> by the Silmarillion master-detail
    /// flow; quest chips become navigable when the Quests tab is registered (#242).
    /// </summary>
    public IReadOnlyList<EntityChipVm> AwardedByQuests { get; }

    /// <summary>
    /// The lorebook this item bestows on use (<see cref="Item.BestowLoreBook"/> resolved via
    /// <c>LorebooksById</c>), or null when the item bestows no book. A single navigable
    /// <see cref="EntityChip"/> — the inbound 1:1 cross-link that's the natural payoff of
    /// the Lorebooks tab shipping (#247). Becomes clickable once the Lorebooks kind target
    /// is registered; degrades to plain text otherwise.
    /// </summary>
    public EntityChipVm? BestowsLorebook { get; }

    /// <summary>
    /// Item sources (NPC vendors, monster drops, quest rewards, …) — rendered as a list of
    /// <see cref="ItemSourceChipVm"/>. Most v1 sources aren't navigable to a tab; the chip
    /// VM carries an <see cref="ItemSourceChipVm.IsNavigable"/> flag that drives clickable
    /// vs. plain-text rendering once Phase 5 ships <c>EntityChip</c>.
    /// </summary>
    public IReadOnlyList<ItemSourceChipVm> Sources { get; }

    /// <summary>
    /// Command invoked when the user clicks a cross-link chip in any of the new sections.
    /// Receives the chip's <see cref="EntityRef"/> as parameter. Null in legacy callers
    /// (Celebrimbor pop-up, deep links) — chips remain plain text when null.
    /// </summary>
    public ICommand? OpenEntityCommand { get; }

    /// <summary>
    /// True when an <see cref="IAugmentPoolPresenter"/> is available — i.e. the Celebrimbor
    /// module is loaded and registered the implementation. Drives the "Browse pool" button
    /// visibility on the AugmentPools section.
    /// </summary>
    public bool HasPoolPresenter => _poolPresenter is not null;

    private static string ResolveSkillDisplayName(IReferenceDataService refData, string skillKey) =>
        refData.Skills.TryGetValue(skillKey, out var s) ? s.DisplayName : skillKey;

    [RelayCommand(CanExecute = nameof(HasPoolPresenter))]
    private void BrowsePool(AugmentPoolPreview? pool)
    {
        if (pool is null || _poolPresenter is null) return;
        _poolPresenter.Show(pool.SourceLabel, pool.ProfileName, pool.MinTier, pool.MaxTier, pool.RecommendedSkill, pool.CraftingTargetLevel, pool.RolledRarityRank, pool.SourceEquipSlot, Item.Name ?? "");
    }
}
