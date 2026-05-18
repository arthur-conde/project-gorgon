using Mithril.Shared.Reference;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Bundle of pre-parsed previews surfaced as collapsible sections on
/// <see cref="ItemDetailWindow"/>. All members are nullable; an unset (or empty) list
/// hides the corresponding section. Lets callers add new preview shapes without
/// growing the <see cref="IItemDetailPresenter"/> overload surface.
/// </summary>
public sealed record ItemDetailContext(
    IReadOnlyList<AugmentPreview>? Augments = null,
    IReadOnlyList<WaxItemPreview>? WaxItems = null,
    IReadOnlyList<WaxAugmentPreview>? WaxAugments = null,
    IReadOnlyList<AugmentPoolPreview>? AugmentPools = null,
    IReadOnlyList<TaughtRecipePreview>? TaughtRecipes = null,
    IReadOnlyList<EffectTagPreview>? EffectTags = null,
    IReadOnlyList<ResearchProgressPreview>? ResearchProgress = null,
    IReadOnlyList<XpGrantPreview>? XpGrants = null,
    IReadOnlyList<WordOfPowerPreview>? WordsOfPower = null,
    IReadOnlyList<LearnedAbilityPreview>? LearnedAbilities = null,
    IReadOnlyList<ItemProducingPreview>? ProducedItems = null,
    IReadOnlyList<EquipBonusPreview>? EquipBonuses = null,
    IReadOnlyList<CraftingEnhancePreview>? CraftingEnhancements = null,
    IReadOnlyList<RecipeCooldownPreview>? RecipeCooldowns = null,
    IReadOnlyList<UnpreviewableExtractionPreview>? UnpreviewableExtractions = null,
    // ── Cross-link sections (populated by the Silmarillion module; null/empty for other callers) ──
    IReadOnlyList<EntityChipVm>? ProducedByRecipes = null,
    IReadOnlyList<EntityChipVm>? ConsumedByRecipes = null,
    // #318 slice 4, surface 2 — Items "Used as" (RecipeIngredientKeyword #259). The
    // capped recipe-chip cluster for the "Used as" section: the first
    // SilmarillionSettings.UsedInChipCap recipes that consume this item via a keyword
    // ingredient slot. The full set + provenance lives in ConsumedAsKeywordInPopup
    // (below); both are projected from the same materialized index collection
    // (IReferenceDataService.RecipesByIngredientKeywordWithReason) so the displayed set
    // cannot diverge. Replaces the retired per-keyword RecipeIngredientKeyword
    // synthetic-kind deep-link chips.
    IReadOnlyList<EntityChipVm>? ConsumedAsKeywordIn = null,
    IReadOnlyList<EntityChipVm>? AwardedByQuests = null,
    // Inbound 1:1 cross-link (#247): the lorebook this item bestows on use, resolved from
    // Item.BestowLoreBook (int? → numeric Book id) via LorebooksById. Null when the item
    // doesn't bestow a book, or the id doesn't resolve. A single navigable EntityChip.
    EntityChipVm? BestowsLorebook = null,
    // Outbound 1:1 cross-link (#435): the Treasure-System pool this item's gear rolls
    // from, resolved from Item.TSysProfile (a single profile name, e.g. "Sword") via
    // EntityRef.Profile. Null when the item carries no TSysProfile. A single navigable
    // EntityChip to the Treasure tab's Profile detail — the "audit existing surfaces
    // when shipping a new EntityKind" wiring for EntityKind.Profile (cookbook). 1:1
    // direct reference ⇒ EntityChip, not a popup (the #318 chip-vs-popup rule).
    EntityChipVm? TreasureProfile = null,
    IReadOnlyList<ItemSourceChipVm>? Sources = null,
    // #318 slice 4, surface 1 — Items "Used in". The reverse-lookup ("recipes that
    // consume this item") is now a provenance popup fed the source index
    // (IReferenceDataService.RecipesByIngredientItemWithReason) directly, replacing the
    // retired RecipeIngredientItem synthetic-kind ActionChip. Non-null whenever the item
    // is consumed by any recipe; the popup is the count-bearing surface, opened by
    // ItemDetailViewModel.ShowConsumedByRecipesPopupCommand. The relationship is
    // single-reason (DirectIngredient) so the popup collapses to a flat list (#318
    // Discipline). There is no query re-derivation — the displayed set cannot diverge
    // from the index.
    ProvenancePopupViewModel? ConsumedByRecipesPopup = null,
    // #318 slice 4, surface 2 — Items "Used as" (RecipeIngredientKeyword #259). The
    // reverse-lookup ("recipes that use this item via a keyword-ingredient slot") is now a
    // provenance popup fed the source index (RecipesByIngredientKeywordWithReason)
    // directly, replacing the retired per-keyword RecipeIngredientKeyword synthetic-kind
    // ActionChips that each deep-linked to `IngredientKeywords CONTAINS "<tag>"`. Non-null
    // whenever any recipe consumes the item via a keyword slot; the popup is the
    // count-bearing surface, opened by
    // ItemDetailViewModel.ShowConsumedAsKeywordInPopupCommand. The relationship is
    // single-reason (KeywordIngredientSlot) so the popup collapses to a flat list (#318
    // Discipline). There is no query re-derivation — the displayed set cannot diverge from
    // the index.
    ProvenancePopupViewModel? ConsumedAsKeywordInPopup = null)
{
    public static ItemDetailContext Empty { get; } = new();
}
