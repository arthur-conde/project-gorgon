using System.Linq;
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

        // ── Phase 5 grammar-primitive projections ──────────────────────────────
        // The legacy string/chip members above stay (tests + planner-facing
        // contract); these are the grammar-tier carriers the view binds. Built
        // here (not in RecipesTabViewModel) because the VM already holds every
        // source datum — the Phase-5 mapping is mechanical, not data-bearing.

        // Fact stat strip (matrix #3): one inert dot-separated value-only strip
        // replacing the three bordered stat-badge boxes. Each segment is a
        // value-only FactPair (null label) and empties are skipped, so the strip
        // self-elides exactly like the old per-chip NullOrEmptyToVis did.
        StatStrip = FactTableVm.Strip(
            new[] { SkillRequirementChip, MaxUsesChip, CooldownChip }
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => new FactPair(null, s))
                .ToList());

        // Link projections (matrix #6/#9/#10/#12). EntityChip/ItemSourceChip →
        // LinkVm via the ratified adapters; the G3-amended adapters now carry the
        // chip's IconId through as the preferred lead sprite. Ingredients still set
        // LinkGlyph.Ingredient explicitly, but per the G3 amendment that glyph is now
        // the Lucide *fallback* only: `with { Glyph = ... }` preserves the IconId from
        // LinkVm.From(c), so a real ingredient sprite renders when present and the
        // flask Lucide shows only for icon-less ingredients (reconciliation flag #3
        // CLOSED — the adapter no longer needs to infer Ingredient when art exists).
        IngredientLinks = Ingredients
            .Select(c => LinkVm.From(c) with { Glyph = LinkGlyph.Ingredient })
            .ToList();
        ProducedItemLinks = ProducedItems.Select(LinkVm.From).ToList();
        SourceLinks = Sources is null
            ? []
            : Sources.Select(LinkVm.From).ToList();

        // Requirement rows: same prose-or-(prefix+link) dual shape, but the inline
        // chip is now a LinkVm (matrix #6). The row record lives in a file outside
        // this pilot's edit scope, so wrap it rather than extend it.
        RequirementRows = Requirements.Select(RecipeRequirementRowVm.From).ToList();
        SharedCooldownRowVm = SharedCooldownRow is null
            ? null
            : RecipeRequirementRowVm.From(SharedCooldownRow);

        // Footer ID: InternalName is a cross-entity reference KEY ⇒ copyable
        // (matrix #14, G-a). None() when absent so the strip self-hides.
        Footer = string.IsNullOrEmpty(InternalName)
            ? FactFooterVm.None()
            : FactFooterVm.Key(InternalName);
    }

    /// <summary>
    /// Inert Fact stat strip (matrix #3) — the dot-separated value-only segments
    /// (<see cref="SkillRequirementChip"/> · <see cref="MaxUsesChip"/> ·
    /// <see cref="CooldownChip"/>, empties skipped) replacing the three legacy
    /// bordered stat-badge boxes. G-b: no box, no gold; the
    /// <c>FactTableLayout.Strip</c> Style carries the (inert) pigment.
    /// </summary>
    public FactTableVm StatStrip { get; }

    /// <summary>
    /// Ingredient cross-links as the unified <see cref="LinkVm"/> (matrix #10).
    /// <see cref="LinkVm.Glyph"/> is set to <see cref="LinkGlyph.Ingredient"/> as the
    /// Lucide <em>fallback</em>; per the G3 amendment the chip's
    /// <see cref="LinkVm.IconId"/> sprite is preferred when present (reconciliation
    /// flag #3 closed — a real ingredient sprite now shows; the flask Lucide is the
    /// icon-less fallback only).
    /// </summary>
    public IReadOnlyList<LinkVm> IngredientLinks { get; }

    /// <summary>Produced-item cross-links as <see cref="LinkVm"/> (matrix #12);
    /// glyph derived from kind by the adapter (Item ⇒ package).</summary>
    public IReadOnlyList<LinkVm> ProducedItemLinks { get; }

    /// <summary>
    /// "Taught by" source rows as <see cref="LinkVm"/> (matrix #9). The provenance
    /// suffix rides from <see cref="ItemSourceChipVm.Detail"/>; glyph by kind.
    /// Empty (never null) so the view's count-based section hide is uniform.
    /// </summary>
    public IReadOnlyList<LinkVm> SourceLinks { get; }

    /// <summary>
    /// <see cref="Requirements"/> reshaped so each row's inline chip is a
    /// <see cref="LinkVm"/> (matrix #6). Wraps the underlying
    /// <see cref="RecipeRequirementRow"/> (whose record lives outside this pilot's
    /// edit scope) rather than mutating it.
    /// </summary>
    public IReadOnlyList<RecipeRequirementRowVm> RequirementRows { get; }

    /// <summary>
    /// <see cref="SharedCooldownRow"/> reshaped to the Link-carrying row VM
    /// (matrix #7 — same template as the requirement rows). Null when absent.
    /// </summary>
    public RecipeRequirementRowVm? SharedCooldownRowVm { get; }

    /// <summary>
    /// Footer identifier strip (matrix #14, G-a). <see cref="InternalName"/> is a
    /// cross-entity reference KEY ⇒ a single copyable cell; <c>None()</c> (the
    /// strip self-hides) when the recipe has no internal name.
    /// </summary>
    public FactFooterVm Footer { get; }

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
        // Matrix #11: the bespoke ghost-gold "{Label} — view all N →" Button
        // becomes the shared Set-reference primitive. Summary-form (MatchCount
        // non-null ⇒ "{Label} · N →"), actionable (the reveal is wired to
        // ShowPopupCommand). Gold→blue is intentional per G-b, not a regression.
        SetRef = new SetRefVm(Label, MatchCount: MatchCount, IsActionable: true);
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

    /// <summary>
    /// The keyword slot as the shared Set-reference primitive (matrix #11): a
    /// summary-form <see cref="SetRefVm"/> (<c>"{Label} · {MatchCount} →"</c>),
    /// actionable — its reveal is <see cref="ShowPopupCommand"/>. Replaces the
    /// bespoke ghost-gold "view all N" Button; the gold→blue shift is the ratified
    /// G-b correction, not a regression.
    /// </summary>
    public SetRefVm SetRef { get; }
}

/// <summary>
/// View-side reshape of a <see cref="RecipeRequirementRow"/> for the Phase-5
/// grammar primitives: the prose-or-(prefix + inline chip) dual shape is
/// preserved, but the inline chip is the unified <see cref="LinkVm"/> instead of
/// the legacy <see cref="EntityChipVm"/> (matrix #6/#7). This wraps rather than
/// extends the underlying record because <see cref="RecipeRequirementRow"/> is
/// declared outside this pilot's edit scope (<c>RecipeRequirementProjector.cs</c>).
/// <see cref="Link"/> is null for a prose-only row (drives the same NullToVis
/// object self-switch the legacy template used on <c>Chip</c>).
/// </summary>
public sealed class RecipeRequirementRowVm
{
    private RecipeRequirementRowVm(string text, string? prefix, LinkVm? link)
    {
        Text = text;
        Prefix = prefix;
        Link = link;
    }

    /// <summary>Accessible / fallback prose rendering (used when <see cref="Link"/> is null).</summary>
    public string Text { get; }

    /// <summary>Inline field-label prefix shown before the <see cref="Link"/> (Structure tier).</summary>
    public string? Prefix { get; }

    /// <summary>
    /// The inline navigable cross-link, or null for a prose-only row. Recipe→recipe
    /// edges (RecipeKnown / RecipeUsed / shared-cooldown) are Recipe-kind, so the
    /// adapter yields the recipe glyph by kind (matrix #6 — acceptable).
    /// </summary>
    public LinkVm? Link { get; }

    /// <summary>
    /// Adapts a legacy <see cref="RecipeRequirementRow"/>: a row carrying a
    /// <see cref="RecipeRequirementRow.Chip"/> becomes a prefix + <see cref="LinkVm"/>
    /// row (chip → <see cref="LinkVm.From(EntityChipVm)"/>); a prose row stays prose
    /// (<see cref="Link"/> null).
    /// </summary>
    public static RecipeRequirementRowVm From(RecipeRequirementRow row) =>
        new(row.Text, row.Prefix, row.Chip is null ? null : LinkVm.From(row.Chip));
}
