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
    IReadOnlyList<EntityChipVm>? ConsumedAsKeywordIn = null,
    IReadOnlyList<EntityChipVm>? AwardedByQuests = null,
    IReadOnlyList<ItemSourceChipVm>? Sources = null,
    // Always-visible navigable summary chip for the "Used in" section. Non-null whenever
    // the item is consumed by any recipe (independent of the chip cap); renders as a
    // RecipeIngredientItem-kind ActionChip that deep-links to the Recipes tab filtered to
    // "Ingredients CONTAINS <itemInternalName>". DisplayName carries the
    // "View all N in Recipes tab →" label.
    EntityChipVm? RecipesTabShortcut = null)
{
    public static ItemDetailContext Empty { get; } = new();
}
