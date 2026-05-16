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
        IReadOnlyList<RecipeKeywordSlotVm>? keywordSlots = null,
        IReadOnlyList<RecipeRequirementRow>? requirements = null,
        EntityChipVm? sharedCooldownChip = null)
    {
        Recipe = recipe;
        Ingredients = ingredients;
        ProducedItems = producedItems;
        ResultEffectsText = resultEffectsText;
        OpenEntityCommand = openEntityCommand;
        SkillDisplayName = skillDisplayName ?? recipe.Skill;
        Sources = sources;
        KeywordSlots = keywordSlots ?? [];
        Requirements = requirements ?? [];
        CostLines = BuildCostLines(recipe.Costs);
        CooldownChip = BuildCooldownChip(recipe.ResetTimeInSeconds);
        // Shared-cooldown is a recipe→recipe edge too, but not a requirement gate — render
        // it through the same row template (prefix + inline chip) so it reads consistently
        // with the requirement chip rows instead of as a separate visual register.
        SharedCooldownRow = sharedCooldownChip is null
            ? null
            : new RecipeRequirementRow(
                $"Shares cooldown with {sharedCooldownChip.DisplayName}",
                "Shares cooldown with",
                sharedCooldownChip);
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
    /// Per-character lifetime use cap, e.g. "Limited to 2 uses". Only Research-keyword
    /// recipes (WeatherWitching/FireMagic/IceMagic) carry <see cref="Recipe.MaxUses"/>;
    /// it is never per-day/per-session. Empty string when absent or non-positive — the
    /// view hides the chip on empty (string-only <c>NullOrEmptyToVis</c>, matching
    /// <see cref="SkillRequirementChip"/>). <c>MaxUses == 1</c> renders singular.
    /// </summary>
    public string MaxUsesChip =>
        Recipe.MaxUses is int n && n > 0
            ? $"Limited to {n} use{(n == 1 ? "" : "s")}"
            : "";

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

    /// <summary>
    /// Ordered <see cref="Recipe.OtherRequirements"/> rows — each either prose or a
    /// sentence with an inline navigable chip (the Quest dual-shape idiom), in authored
    /// order so cross-links read in the same flow as the prose gates rather than as an
    /// orphaned pill cluster. Covers the time/RNG-cyclical and user-asserted unlocks the
    /// planner deliberately punts on (<c>docs/planner-recipe-field-consumption.md</c>).
    /// Empty when the recipe has none; the view hides the section on a zero count. Built
    /// by <see cref="RecipeRequirementProjector"/> in <see cref="RecipesTabViewModel"/>.
    /// </summary>
    public IReadOnlyList<RecipeRequirementRow> Requirements { get; }

    /// <summary>
    /// Currency cost lines from <see cref="Recipe.Costs"/> (e.g. "1,500 Councils"). The
    /// planner ignores <c>Costs</c> by design (it doesn't change XP or craft count) — so
    /// the browser is the only surface that can tell the player a recipe costs money.
    /// Empty when absent.
    /// </summary>
    public IReadOnlyList<string> CostLines { get; }

    /// <summary>
    /// Reuse-cooldown chip (e.g. "Reuse every 1h 30m") from
    /// <see cref="Recipe.ResetTimeInSeconds"/>. Sits beside <see cref="MaxUsesChip"/>:
    /// the planner is time-stateless so it surfaces the cap half but not this — closing
    /// the consistency break called out in <c>docs/silmarillion-field-coverage.md</c>.
    /// Empty string when absent (view hides on empty, string-only NullOrEmptyToVis).
    /// </summary>
    public string CooldownChip { get; }

    /// <summary>
    /// Navigable cross-link row for <see cref="Recipe.SharesResetTimerWith"/> — every
    /// value in the corpus (19/19) is a real recipe <c>InternalName</c>, so this is a
    /// recipe→recipe edge, not prose. Same <see cref="RecipeRequirementRow"/> shape as
    /// <see cref="Requirements"/> so it renders through the identical prefix+inline-chip
    /// template, but exposed separately and labelled "Shares cooldown with" because a
    /// shared-cooldown grouping is not a requirement gate. Null when the field is absent.
    /// </summary>
    public RecipeRequirementRow? SharedCooldownRow { get; }

    private static IReadOnlyList<string> BuildCostLines(IReadOnlyList<RecipeCost>? costs)
    {
        if (costs is null || costs.Count == 0) return [];
        var lines = new List<string>(costs.Count);
        foreach (var c in costs)
        {
            if (c.Price <= 0) continue;
            lines.Add($"{c.Price:N0} {FriendlyCurrency(c.Currency)}");
        }
        return lines;
    }

    // Recipe Costs currencies in the bundled corpus are exotic crafting currencies
    // (FaeEnergy, CombatWisdom, GlamourCredits, GuildCredits) — never Councils/Cera.
    // Camel-split the token so it reads "Fae Energy" not the raw id, per the
    // skill-key→display-name convention. Single-token names ("Cera") pass through.
    private static string FriendlyCurrency(string? currency) =>
        string.IsNullOrEmpty(currency)
            ? "currency"
            : System.Text.RegularExpressions.Regex.Replace(currency, "(?<=[a-z])([A-Z])", " $1");

    /// <summary>Seconds → compact "1d 2h", "1h 30m", "45m", "30s" (largest two units).</summary>
    private static string BuildCooldownChip(int? resetSeconds)
    {
        if (resetSeconds is not { } secs || secs <= 0) return "";
        var d = secs / 86400;
        var h = secs % 86400 / 3600;
        var m = secs % 3600 / 60;
        var s = secs % 60;
        var parts = new List<string>(2);
        if (d > 0) parts.Add($"{d}d");
        if (h > 0) parts.Add($"{h}h");
        if (m > 0 && parts.Count < 2) parts.Add($"{m}m");
        if (s > 0 && parts.Count < 2) parts.Add($"{s}s");
        return $"Reuse every {string.Join(" ", parts)}";
    }
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
