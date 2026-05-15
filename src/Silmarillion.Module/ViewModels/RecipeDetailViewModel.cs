using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Read-only projection of a <see cref="Recipe"/> for the Silmarillion module's recipe
/// detail pane. Hostable in both the master-detail right pane and the popup
/// <see cref="Silmarillion.Views.RecipeDetailWindow"/>.
/// Cross-link projections (ingredient/result chips) are supplied by the page-level
/// view-model, which has access to <see cref="IReferenceDataService"/> and the navigator's
/// <c>CanOpen</c> for the <see cref="EntityChipVm.IsNavigable"/> flag.
/// </summary>
public sealed class RecipeDetailViewModel
{
    /// <summary>
    /// Host-supplied opener for a keyword-slot's "matching items" provenance popup
    /// (#318 slice 4, surface 3 — retiring the synthetic <c>ItemKeyword</c> #270 deep
    /// link). Defaults to <see cref="ShowProvenancePopupWindow"/> (creates +
    /// <c>Show()</c>s a <see cref="ProvenancePopupWindow"/>). Tests swap in a capturing
    /// delegate so the VM is fully assertable without spawning a window. Opening the
    /// popup this way never calls <c>IReferenceNavigator</c>, so it pushes no
    /// back/forward history — identical non-navigating contract to
    /// <c>IReferenceKindTarget.TryOpenInWindow</c> (#229) and to
    /// <c>ItemDetailViewModel.ProvenancePopupOpener</c> (the surface-1 reference).
    /// </summary>
    public static Action<ProvenancePopupViewModel, ICommand?> ProvenancePopupOpener { get; set; }
        = ShowProvenancePopupWindow;

    private static void ShowProvenancePopupWindow(ProvenancePopupViewModel vm, ICommand? chipClick) =>
        new ProvenancePopupWindow { DataContext = vm, ChipClickCommand = chipClick }.Show();

    public RecipeDetailViewModel(
        Recipe recipe,
        IReadOnlyList<EntityChipVm> ingredients,
        IReadOnlyList<EntityChipVm> producedItems,
        IReadOnlyList<string> resultEffectsText,
        ICommand? openEntityCommand = null,
        string? skillDisplayName = null,
        IReadOnlyList<ItemSourceChipVm>? sources = null,
        IReadOnlyList<RecipeKeywordSlotVm>? keywordSlots = null)
    {
        Recipe = recipe;
        Ingredients = ingredients;
        ProducedItems = producedItems;
        ResultEffectsText = resultEffectsText;
        OpenEntityCommand = openEntityCommand;
        SkillDisplayName = skillDisplayName ?? recipe.Skill;
        Sources = sources;
        KeywordSlots = keywordSlots ?? [];
    }

    /// <summary>
    /// Human-readable skill name (resolved by the page VM via <c>IReferenceDataService.Skills</c>),
    /// falling back to the internal name if resolution fails. Drives <see cref="SkillRequirementChip"/>.
    /// </summary>
    public string? SkillDisplayName { get; }

    public Recipe Recipe { get; }

    public string DisplayName => Recipe.Name ?? Recipe.InternalName ?? Recipe.Key;
    public string InternalName => Recipe.InternalName ?? "";
    public string? Description => Recipe.Description;
    public string? Skill => Recipe.Skill;
    public int SkillLevelReq => Recipe.SkillLevelReq;
    public int IconId => Recipe.IconId;

    /// <summary>
    /// Combined "Skill Level" chip (e.g. "Cooking 30"). Empty when no skill is set —
    /// caller should hide the chip border on empty strings.
    /// </summary>
    public string SkillRequirementChip =>
        string.IsNullOrEmpty(SkillDisplayName) ? "" : $"{SkillDisplayName} {Recipe.SkillLevelReq}";

    /// <summary>
    /// Direct item-ingredient chips only (1:1 <see cref="EntityRef.Item"/> references) —
    /// keyword slots are <em>not</em> in this list any more (#318 slice 4, surface 3):
    /// a keyword slot is a 1:N fan-out and now surfaces via <see cref="KeywordSlots"/>'s
    /// provenance popup, per the #318 chip-vs-popup rule.
    /// </summary>
    public IReadOnlyList<EntityChipVm> Ingredients { get; }

    /// <summary>
    /// Keyword-slot rows for this recipe (#318 slice 4, surface 3). Each is a recipe
    /// <see cref="RecipeKeywordIngredient"/> slot ("any Crystal", "Main-Hand Item") that
    /// fans out to N items satisfying its keyword constraint. Per the #318 chip-vs-popup
    /// rule a 1:N fan-out is a provenance popup, not a navigable chip: each row carries a
    /// <see cref="ProvenancePopupViewModel"/> built from
    /// <c>IReferenceDataService.ItemsByRecipeKeywordSlotWithReason</c> directly, opened by
    /// <see cref="RecipeKeywordSlotVm.ShowPopupCommand"/> with no navigator history pushed.
    /// Empty when the recipe has no keyword slots — drives the section hide in
    /// <see cref="Views.RecipeDetailView"/>.
    /// </summary>
    public IReadOnlyList<RecipeKeywordSlotVm> KeywordSlots { get; }

    public IReadOnlyList<EntityChipVm> ProducedItems { get; }

    /// <summary>
    /// TODO(stub:#214): plain-string rendering of recipe ResultEffects. Replaced by rich
    /// chip templates in #214.
    /// </summary>
    public IReadOnlyList<string> ResultEffectsText { get; }

    /// <summary>
    /// Command invoked when the user clicks an ingredient/produced chip. Receives the chip's
    /// <see cref="EntityRef"/>. Wired by <see cref="RecipesTabViewModel"/> to the navigator.
    /// </summary>
    public ICommand? OpenEntityCommand { get; }

    /// <summary>
    /// Where this recipe comes from (NPC trainer, scroll/effect, quest reward, …). Pulled
    /// from <c>IReferenceDataService.RecipeSources</c>. Null when no sources are known —
    /// drives the empty-section hide in <see cref="Views.RecipeDetailView"/>. Mirrors the
    /// <c>Sources</c> shape used by <see cref="ItemDetailViewModel"/>.
    /// </summary>
    public IReadOnlyList<ItemSourceChipVm>? Sources { get; }
}

/// <summary>
/// One recipe keyword-slot row in the recipe-detail "Keyword ingredients" section
/// (#318 slice 4, surface 3). A keyword slot is a 1:N fan-out (one slot → N matching
/// items), so per the #318 chip-vs-popup rule it surfaces as a provenance popup fed the
/// source index directly, never a navigable synthetic-kind chip. <see cref="Label"/> is
/// the slot's friendly description ("any Crystal", "Main-Hand Item"); <see cref="Popup"/>
/// is the <see cref="ProvenancePopupViewModel"/> over the slot's matching items
/// (single-reason ⇒ flat list); <see cref="MatchCount"/> equals
/// <see cref="ProvenancePopupViewModel.TotalCount"/> and drives the "View all N →" label.
/// </summary>
public sealed class RecipeKeywordSlotVm
{
    public RecipeKeywordSlotVm(string label, ProvenancePopupViewModel popup, ICommand? chipClickCommand)
    {
        Label = label;
        Popup = popup;
        MatchCount = popup.TotalCount;
        ShowPopupCommand = new RelayCommand(
            () => RecipeDetailViewModel.ProvenancePopupOpener(popup, chipClickCommand));
    }

    /// <summary>Friendly slot description, e.g. "any Crystal" / "Main-Hand Item".</summary>
    public string Label { get; }

    /// <summary>
    /// The provenance popup for this slot's matching items. Built from
    /// <c>IReferenceDataService.ItemsByRecipeKeywordSlotWithReason</c> directly (membership
    /// + provenance); single-reason (<c>KeywordMatch</c>) so it renders as a flat list per
    /// the #318 Discipline rule.
    /// </summary>
    public ProvenancePopupViewModel Popup { get; }

    /// <summary>
    /// Distinct count of items satisfying this slot — equals
    /// <see cref="ProvenancePopupViewModel.TotalCount"/>. Drives the "View all N →" label.
    /// May be 0 (a slot whose constraint no item currently satisfies): the row still
    /// renders so the recipe's ingredient shape is legible, but the affordance reads
    /// "View all 0 →".
    /// </summary>
    public int MatchCount { get; }

    /// <summary>
    /// Opens <see cref="Popup"/> via <see cref="RecipeDetailViewModel.ProvenancePopupOpener"/>.
    /// The popup is a window shown directly — opening it pushes no navigator history
    /// (#229 contract; mirrors the surface-1 <c>ItemDetailViewModel</c> command).
    /// </summary>
    public ICommand ShowPopupCommand { get; }
}
