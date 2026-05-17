using System.Linq;
using System.Text.RegularExpressions;
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
        ConsumedAsKeywordInPopup = context.ConsumedAsKeywordInPopup;
        ConsumedAsKeywordInTotal = context.ConsumedAsKeywordInPopup?.TotalCount ?? 0;
        ShowConsumedAsKeywordInPopupCommand = new RelayCommand(
            () => ProvenancePopupOpener(ConsumedAsKeywordInPopup!, OpenEntityCommand),
            () => ConsumedAsKeywordInPopup is not null);
        AwardedByQuests = context.AwardedByQuests ?? [];
        BestowsLorebook = context.BestowsLorebook;
        Sources = context.Sources ?? [];
        _poolPresenter = poolPresenter;

        // ── #424 grammar-primitive projections (additive) ──────────────────────
        // The legacy chip/string/command members assigned above STAY (the
        // ItemsTabViewModel tests + the cross-module detail contract assert
        // them); these are the #404 grammar-tier carriers the migrated
        // ItemDetailView binds. Mechanical mapping — a consistency-diff vs the
        // merged EffectDetailView fan-out (#418) + the Recipe pilot, not fresh
        // design.

        // One inert Fact stat strip under the title (pilot StatStrip /
        // EffectDetailView MetadataStrip): EquipSlot (camel-split, labelled) +
        // every skill requirement as a value-only segment. No box, no gold
        // (G-b) — the shared FactTable Strip Style owns the inert pigment.
        // Empties skipped; an all-empty strip renders nothing (StripText "").
        var stat = new List<FactPair>(1 + SkillReqChips.Count);
        if (!string.IsNullOrEmpty(EquipSlot))
            stat.Add(new FactPair("Equip slot", SplitCamel(EquipSlot!)));
        foreach (var req in SkillReqChips)
            stat.Add(new FactPair(null, req));
        StatStrip = FactTableVm.Strip(stat);

        // Cross-link sections → the unified Link (matrix #6/#9/#10/#12), via the
        // ratified EntityChip/ItemSourceChip adapters; the view renders them
        // Density="List" (the canonical pilot enumeration pattern). Glyph is
        // kind-derived by the adapter; the chip IconId rides through as the
        // preferred lead sprite per the G3 amendment.
        SourceLinks = Sources.Select(LinkVm.From).ToList();
        ProducedByRecipeLinks = ProducedByRecipes.Select(LinkVm.From).ToList();
        AwardedByQuestLinks = AwardedByQuests.Select(LinkVm.From).ToList();
        ConsumedByRecipeLinks = ConsumedByRecipes.Select(LinkVm.From).ToList();
        ConsumedAsKeywordInLinks = ConsumedAsKeywordIn.Select(LinkVm.From).ToList();
        BestowsLorebookLink = BestowsLorebook is null ? null : LinkVm.From(BestowsLorebook);

        // "View all N →" drawers → summary-form Set-reference (matrix #11 /
        // EffectDetailView §7 "Required by abilities"), actionable via the
        // EXISTING popup commands (gold→blue per ratified G-b). Null when no
        // recipe relates ⇒ the SetRef row hides; the capped Link cluster carries
        // the rest. The count mirrors the popup's TotalCount (cannot diverge).
        ConsumedByRecipesSetRef = ConsumedByRecipesPopup is null
            ? null
            : new SetRefVm("Used in", MatchCount: ConsumedByRecipesTotal, IsActionable: true);
        ConsumedAsKeywordInSetRef = ConsumedAsKeywordInPopup is null
            ? null
            : new SetRefVm("Used as", MatchCount: ConsumedAsKeywordInTotal, IsActionable: true);

        // Footer ID (matrix #14 · G-a · ratified E5). The Item InternalName is a
        // cross-entity reference KEY — recipes/quests/NPCs resolve items by it —
        // ⇒ the copyable `KEY` cell (NOT the inert storage-only `ROW`; cf.
        // EffectDetailView's EnvelopeKey). None() self-hides when absent.
        Footer = string.IsNullOrEmpty(InternalName)
            ? FactFooterVm.None()
            : FactFooterVm.Key(InternalName);
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
    /// Recipes that consume this item via a keyword-ingredient slot, for the item-detail
    /// "Used as" section (#318 slice 4, surface 2 — <c>RecipeIngredientKeyword</c> #259).
    /// Capped at <c>SilmarillionSettings.UsedInChipCap</c>; the full set + provenance is
    /// always reachable via the "View all N →" affordance that opens
    /// <see cref="ConsumedAsKeywordInPopup"/>. Replaces the retired per-keyword
    /// <c>RecipeIngredientKeyword</c> synthetic-kind deep-link chips — there is no query
    /// re-derivation, so the displayed set cannot diverge from the index.
    /// </summary>
    public IReadOnlyList<EntityChipVm> ConsumedAsKeywordIn { get; }

    /// <summary>
    /// The "Used as" provenance popup VM opened by
    /// <see cref="ShowConsumedAsKeywordInPopupCommand"/>, or <see langword="null"/> when no
    /// recipe consumes this item via a keyword slot (#318 slice 4, surface 2). Built from
    /// <c>IReferenceDataService.RecipesByIngredientKeywordWithReason</c> directly
    /// (membership + provenance), replacing the retired <c>RecipeIngredientKeyword</c>
    /// synthetic-kind deep links — there is no query re-derivation, so the displayed set
    /// cannot diverge from the index. The relationship is single-reason
    /// (<c>KeywordIngredientSlot</c>) so the popup collapses to a flat list (#318
    /// Discipline).
    /// </summary>
    public ProvenancePopupViewModel? ConsumedAsKeywordInPopup { get; }

    /// <summary>
    /// Distinct count of recipes that consume this item via a keyword-ingredient slot —
    /// equals <see cref="ProvenancePopupViewModel.TotalCount"/> of
    /// <see cref="ConsumedAsKeywordInPopup"/>. Drives the "View all N →" label. 0 ⇒ no
    /// relationship and the whole "Used as" section hides.
    /// </summary>
    public int ConsumedAsKeywordInTotal { get; }

    /// <summary>
    /// Opens <see cref="ConsumedAsKeywordInPopup"/> via <see cref="ProvenancePopupOpener"/>.
    /// Bound to the always-visible "View all N →" affordance. The popup is a window shown
    /// directly — opening it pushes no navigator history (#229 contract; mirrors the
    /// surface-1 <see cref="ShowConsumedByRecipesPopupCommand"/> / the slice-2
    /// <c>EffectDetailViewModel.ShowRequiredByAbilitiesPopupCommand</c>).
    /// </summary>
    public ICommand ShowConsumedAsKeywordInPopupCommand { get; }

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

    // ── #424 grammar-primitive carriers (additive; the legacy members above
    //    stay — the ItemsTabViewModel tests + the cross-module detail contract
    //    assert them). The migrated ItemDetailView binds these. ───────────────

    /// <summary>
    /// One inert Fact stat strip under the title (matrix #3 — the pilot
    /// <c>StatStrip</c> / <c>EffectDetailViewModel.MetadataStrip</c> idiom):
    /// EquipSlot (camel-split, labelled) + every <see cref="SkillReqChips"/>
    /// entry as a value-only segment. G-b: no box, no gold — the shared
    /// <c>FactTable</c> Strip Style owns the inert pigment. Empties skipped; an
    /// all-empty strip renders nothing.
    /// </summary>
    public FactTableVm StatStrip { get; }

    /// <summary>"Sources" rows as the unified <see cref="LinkVm"/> (matrix #9 —
    /// provenance suffix rides from <see cref="ItemSourceChipVm.Detail"/>).</summary>
    public IReadOnlyList<LinkVm> SourceLinks { get; }

    /// <summary>"Produced by" recipe cross-links as <see cref="LinkVm"/> (matrix #12).</summary>
    public IReadOnlyList<LinkVm> ProducedByRecipeLinks { get; }

    /// <summary>"Awarded by" quest cross-links as <see cref="LinkVm"/> (matrix #10).</summary>
    public IReadOnlyList<LinkVm> AwardedByQuestLinks { get; }

    /// <summary>"Used in" capped recipe cluster as <see cref="LinkVm"/> (matrix #10).
    /// The full set is the <see cref="ConsumedByRecipesSetRef"/> drawer.</summary>
    public IReadOnlyList<LinkVm> ConsumedByRecipeLinks { get; }

    /// <summary>"Used as" capped recipe cluster as <see cref="LinkVm"/> (matrix #10).
    /// The full set is the <see cref="ConsumedAsKeywordInSetRef"/> drawer.</summary>
    public IReadOnlyList<LinkVm> ConsumedAsKeywordInLinks { get; }

    /// <summary>"Bestows lorebook" inbound 1:1 cross-link as a single
    /// <see cref="LinkVm"/> (matrix #6), or null when the item bestows no book
    /// (the section hides — bind via the object-safe <c>NullToVis</c>).</summary>
    public LinkVm? BestowsLorebookLink { get; }

    /// <summary>
    /// The "Used in · N matches →" drawer as a summary-form Set-reference
    /// (matrix #11), actionable via <see cref="ShowConsumedByRecipesPopupCommand"/>.
    /// Null when no recipe consumes this item (the SetRef row hides). Gold→blue
    /// per ratified G-b. <see cref="SetRefVm.MatchCount"/> mirrors
    /// <see cref="ConsumedByRecipesTotal"/> so it cannot diverge from the popup.
    /// </summary>
    public SetRefVm? ConsumedByRecipesSetRef { get; }

    /// <summary>
    /// The "Used as · N matches →" drawer as a summary-form Set-reference
    /// (matrix #11), actionable via <see cref="ShowConsumedAsKeywordInPopupCommand"/>.
    /// Null when no recipe consumes this item via a keyword slot. Gold→blue per
    /// ratified G-b; count mirrors <see cref="ConsumedAsKeywordInTotal"/>.
    /// </summary>
    public SetRefVm? ConsumedAsKeywordInSetRef { get; }

    /// <summary>
    /// Footer identifier strip (matrix #14 · G-a · ratified E5). The Item
    /// <see cref="InternalName"/> is a cross-entity reference KEY (recipes /
    /// quests / NPCs resolve items by it) ⇒ a single copyable <c>KEY</c> cell;
    /// <c>None()</c> (the strip self-hides) when the item has no internal name.
    /// </summary>
    public FactFooterVm Footer { get; }

    /// <summary>
    /// True when an <see cref="IAugmentPoolPresenter"/> is available — i.e. the Celebrimbor
    /// module is loaded and registered the implementation. Drives the "Browse pool" button
    /// visibility on the AugmentPools section.
    /// </summary>
    public bool HasPoolPresenter => _poolPresenter is not null;

    private static string ResolveSkillDisplayName(IReferenceDataService refData, string skillKey) =>
        refData.Skills.TryGetValue(skillKey, out var s) ? s.DisplayName : skillKey;

    // EquipSlot is an id-shaped token ("MainHand" → "Main Hand"); split on the
    // lowercase→uppercase boundary so the Fact strip reads as a display value,
    // per the skill-key→display-name convention. The XAML previously did this
    // with the CamelCaseSplit converter; the migrated view binds one strip
    // string, so the split moves VM-side (single source of truth, testable).
    // Single-token slots ("Chest") pass through unchanged.
    private static string SplitCamel(string token) =>
        Regex.Replace(token, "(?<=[a-z])([A-Z])", " $1");

    [RelayCommand(CanExecute = nameof(HasPoolPresenter))]
    private void BrowsePool(AugmentPoolPreview? pool)
    {
        if (pool is null || _poolPresenter is null) return;
        _poolPresenter.Show(pool.SourceLabel, pool.ProfileName, pool.MinTier, pool.MaxTier, pool.RecommendedSkill, pool.CraftingTargetLevel, pool.RolledRarityRank, pool.SourceEquipSlot, Item.Name ?? "");
    }
}
