using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Mithril.Reference.Models.Abilities;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Read-only projection of an <see cref="Ability"/> for the Silmarillion Abilities tab detail
/// pane. Hostable in both the master-detail right pane and the popup
/// <see cref="Silmarillion.Views.AbilityDetailWindow"/>.
/// <para>
/// The Ability POCO is wide-but-flat (~80 properties on the top level plus the nested
/// <see cref="AbilityPvE"/> block). This VM groups them into the 13 player-facing sections from
/// the plan (header, description, core mechanics, prerequisites, PvE stats, conditional
/// behaviors, ammo, environmental, special targeting, pet-related, internal flags, sources,
/// footer) so the XAML renders one labeled section per group.
/// </para>
/// <para>
/// Cross-link chips (Prerequisite / UpgradeOf / SharesResetTimerWith → other abilities;
/// ItemKeywordReqs → Items tab keyword filter; Sources → trainer NPCs; reverse-lookups in the
/// group + upgrade-to rosters) are built here so the view-model owns the
/// <c>_navigator.CanOpen</c> calls — chips degrade to plain text automatically when their
/// target kind isn't tabbed yet, per the cookbook's "let CanOpen decide" rule.
/// </para>
/// </summary>
public sealed class AbilityDetailViewModel
{
    public AbilityDetailViewModel(
        Ability ability,
        string internalName,
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver,
        ICommand? openEntityCommand = null)
    {
        Ability = ability;
        InternalName = internalName;
        DisplayName = nameResolver.Resolve(EntityRef.Ability(internalName));
        SkillDisplayName = ResolveSkillDisplay(refData, ability.Skill);
        AbilityGroupDisplayName = ResolveAbilityGroupDisplay(ability);

        PrerequisiteChip = BuildAbilityChip(ability.Prerequisite, nameResolver, navigator);
        UpgradeOfChip = BuildAbilityChip(ability.UpgradeOf, nameResolver, navigator);
        SharesResetTimerWithChip = BuildAbilityChip(ability.SharesResetTimerWith, nameResolver, navigator);

        (ItemKeywordReqChips, FormGateLabels) = PartitionItemKeywordReqs(
            ability.ItemKeywordReqs, ability.ItemKeywordReqErrorMessage, navigator);
        EffectKeywordReqChips = BuildEffectKeywordChips(ability.EffectKeywordReqs, navigator);
        TargetEffectKeywordReqChip = BuildSingleEffectKeywordChip(ability.TargetEffectKeywordReq, navigator);
        SpecialCasterRequirementLabels = BuildSpecialCasterRequirementLabels(ability.SpecialCasterRequirements);

        CostRows = BuildCostRows(ability.Costs);
        ConditionalKeywordRows = BuildConditionalKeywordRows(ability.ConditionalKeywords, navigator);
        AmmoKeywordRows = BuildAmmoKeywordRows(ability.AmmoKeywords, ability.AmmoDescription, navigator);

        EnvironmentalFlags = BuildEnvironmentalFlags(ability);
        SpecialTargetingFlags = BuildSpecialTargetingFlags(ability);
        PetFlags = BuildPetFlags(ability);
        InternalFlags = BuildInternalFlags(ability);

        PvEStats = BuildPvEStats(ability.PvE);
        SpecialValueRows = BuildSpecialValueRows(ability.PvE);
        DoTRows = BuildDoTRows(ability.PvE);

        AbilitiesInGroupChips = BuildAbilitiesInGroupChips(ability, refData, nameResolver, navigator);
        UpgradesToChips = BuildUpgradesToChips(ability, refData, nameResolver, navigator);

        SourceChips = BuildSourceChips(refData, nameResolver, navigator);

        OpenEntityCommand = openEntityCommand;

        // ── Phase 5 grammar-primitive projections ──────────────────────────────
        // Legacy chip/row/string members above stay (the existing tab tests +
        // the detail-pane contract); these are the grammar-tier carriers the
        // view binds. Ability is the largest view — #404 Phase-2 maps it as:
        //   • single ability refs (Prerequisite / UpgradeOf / SharesResetTimer)
        //     + roster/sources = Link; keyword chips (ItemByKeyword /
        //     EffectKeyword filters) = Set-reference (ratified E4, NOT Link);
        //   • the header skill/rank/group badges + Core-mechanics + flag groups
        //     = inert label-value Fact ⇒ FactTable (Strip / Grid); the named
        //     G-b "Ability Rank" gold (T11) dies by construction (no brush);
        //   • FormGate / SpecialCaster bordered non-nav tags = T1-as-Fact ❌
        //     (caster-state prose, NOT filters) → de-boxed inert Fact;
        //   • InternalName footer = a cross-entity reference key (abilities
        //     prereq/upgrade each other; NPCs teach by it) ⇒ copyable KEY.
        // Records that live in this file are projected directly; none are
        // out of scope here.

        HeaderStrip = FactTableVm.Strip(BuildHeaderPairs());
        CoreMechanicsStrip = FactTableVm.Strip(BuildCoreMechanicsPairs());
        CostFact = FactTableVm.Grid(CostRows
            .Select(c => new FactPair(c.Currency, c.Price.ToString("N0"))).ToList());
        PvEStatsFact = FlagGrid(PvEStats);
        SpecialValuesFact = FactTableVm.Grid(SpecialValueRows
            .Select(s => new FactPair(s.Label, s.Display)).ToList());
        EnvironmentalFact = FlagGrid(EnvironmentalFlags);
        SpecialTargetingFact = FlagGrid(SpecialTargetingFlags);
        PetFact = FlagGrid(PetFlags);

        PrerequisiteLink = PrerequisiteChip is null ? null : LinkVm.From(PrerequisiteChip);
        UpgradeOfLink = UpgradeOfChip is null ? null : LinkVm.From(UpgradeOfChip);
        SharesResetTimerWithLink =
            SharesResetTimerWithChip is null ? null : LinkVm.From(SharesResetTimerWithChip);

        AbilitiesInGroupLinks = AbilitiesInGroupChips.Select(LinkVm.From).ToList();
        UpgradesToLinks = UpgradesToChips.Select(LinkVm.From).ToList();
        SourceLinks = SourceChips.Select(LinkVm.From).ToList();

        // Keyword filters → tag-form Set-refs (ratified E4). Per-chip Activate
        // bridges SetRef's VM-param click to OpenEntityCommand(reference);
        // unwired ⇒ availability corollary (blue chassis, safe no-op).
        ItemKeywordReqSetRefs = ItemKeywordReqChips.Select(BuildFilterSetRef).ToList();
        EffectKeywordReqSetRefs = EffectKeywordReqChips.Select(BuildFilterSetRef).ToList();
        TargetEffectKeywordReqSetRef =
            TargetEffectKeywordReqChip is null ? null : BuildFilterSetRef(TargetEffectKeywordReqChip);

        ConditionalRowVms = ConditionalKeywordRows
            .Select(r => new AbilityConditionalRowVm(
                r.Condition,
                r.Keyword,
                r.EffectKeywordChip is null ? null : BuildFilterSetRef(r.EffectKeywordChip)))
            .ToList();
        AmmoRowVms = AmmoKeywordRows
            .Select(r => new AbilityAmmoRowVm(BuildFilterSetRef(r.Chip), r.Count))
            .ToList();

        Footer = string.IsNullOrEmpty(InternalName)
            ? FactFooterVm.None()
            : FactFooterVm.Key(InternalName);
    }

    private static FactTableVm FlagGrid(IReadOnlyList<AbilityFlagRow> rows) =>
        FactTableVm.Grid(rows.Select(r => new FactPair(r.Label, r.Value)).ToList());

    private List<FactPair> BuildHeaderPairs()
    {
        // The header skill/rank/group BADGE BOXES collapse into ONE inert Fact
        // strip (the pilot StatStrip pattern). The Rank box was the named G-b
        // "Ability Rank" gold-tinted badge (T11); FactTableVm carries no brush,
        // so the gold is gone by construction.
        var p = new List<FactPair>(3);
        if (!string.IsNullOrEmpty(SkillLevelDisplay)) p.Add(new FactPair(null, SkillLevelDisplay));
        if (!string.IsNullOrEmpty(Rank)) p.Add(new FactPair(null, Rank!));
        if (!string.IsNullOrEmpty(AbilityGroupDisplayName))
            p.Add(new FactPair("Group", AbilityGroupDisplayName!));
        return p;
    }

    private List<FactPair> BuildCoreMechanicsPairs()
    {
        var p = new List<FactPair>(3);
        if (!string.IsNullOrEmpty(Target)) p.Add(new FactPair("Target", Target!));
        p.Add(new FactPair("Reset time", $"{ResetTime:0.##}s"));
        if (CombatRefreshBaseAmount is { } cr)
            p.Add(new FactPair("Combat refresh", cr.ToString()));
        return p;
    }

    private AbilityFilterSetRefVm BuildFilterSetRef(EntityChipVm chip)
    {
        var wired = chip.IsNavigable && OpenEntityCommand is not null;
        var activate = wired
            ? new RelayCommand(() => OpenEntityCommand!.Execute(chip.Reference))
            : null;
        return new AbilityFilterSetRefVm(
            new SetRefVm(chip.DisplayName, MatchCount: null, IsActionable: wired),
            activate);
    }

    public Ability Ability { get; }
    public string InternalName { get; }
    public string DisplayName { get; }
    public int IconID => Ability.IconID;
    public string? SkillDisplayName { get; }
    public int Level => Ability.Level;

    /// <summary>
    /// Combined skill + level chip text — "Sword 7" when skill is known, "Level 7" otherwise.
    /// Matches the cookbook's master-list precedent so the list-card "Sword 7" reads the same
    /// when promoted into the detail header chip.
    /// </summary>
    public string SkillLevelDisplay =>
        string.IsNullOrEmpty(SkillDisplayName)
            ? $"Level {Level}"
            : $"{SkillDisplayName} {Level}";
    public string? Rank => Ability.Rank;
    public string? AbilityGroupDisplayName { get; }

    public string? Description => Ability.Description;
    public string? Target => Ability.Target;
    public double ResetTime => Ability.ResetTime;
    public int? CombatRefreshBaseAmount => Ability.CombatRefreshBaseAmount;

    public EntityChipVm? PrerequisiteChip { get; }
    public EntityChipVm? UpgradeOfChip { get; }
    public EntityChipVm? SharesResetTimerWithChip { get; }

    public IReadOnlyList<EntityChipVm> ItemKeywordReqChips { get; }

    /// <summary>
    /// Form-gate labels peeled out of <see cref="Ability.ItemKeywordReqs"/>. PG packs
    /// caster-state requirements ("must be in Cow form") into the same array as real item
    /// keywords; routing them through the Items tab produces dead links, so they surface here
    /// as plain-text chips instead. Label is <see cref="Ability.ItemKeywordReqErrorMessage"/>
    /// verbatim — that's the player-facing string and is always populated in the corpus
    /// (corpus walk 2026-05-14, see #303).
    /// </summary>
    public IReadOnlyList<string> FormGateLabels { get; }

    /// <summary>
    /// Required effect-keyword chips — each chip points at the Effects tab filtered by
    /// <c>Keywords CONTAINS "&lt;tag&gt;"</c> via <see cref="EntityRef.EffectKeyword"/>.
    /// Empty when the ability has no <c>EffectKeywordReqs</c>.
    /// </summary>
    public IReadOnlyList<EntityChipVm> EffectKeywordReqChips { get; }

    /// <summary>
    /// Single chip for <see cref="Ability.TargetEffectKeywordReq"/> — the targeting-gate
    /// keyword that the ability requires the target effect to carry. Null when unset.
    /// Surfaced in the Special-Targeting section above the flag rows.
    /// </summary>
    public EntityChipVm? TargetEffectKeywordReqChip { get; }

    public IReadOnlyList<string> SpecialCasterRequirementLabels { get; }

    public IReadOnlyList<AbilityCostRow> CostRows { get; }
    public IReadOnlyList<AbilityConditionalKeywordRow> ConditionalKeywordRows { get; }
    public IReadOnlyList<AbilityAmmoKeywordRow> AmmoKeywordRows { get; }

    public IReadOnlyList<AbilityFlagRow> EnvironmentalFlags { get; }
    public IReadOnlyList<AbilityFlagRow> SpecialTargetingFlags { get; }
    public IReadOnlyList<AbilityFlagRow> PetFlags { get; }
    public IReadOnlyList<AbilityFlagRow> InternalFlags { get; }

    public IReadOnlyList<AbilityFlagRow> PvEStats { get; }
    public IReadOnlyList<AbilitySpecialValueRow> SpecialValueRows { get; }
    public IReadOnlyList<AbilityDoTRow> DoTRows { get; }

    public IReadOnlyList<EntityChipVm> AbilitiesInGroupChips { get; }
    public IReadOnlyList<EntityChipVm> UpgradesToChips { get; }

    public IReadOnlyList<EntityChipVm> SourceChips { get; }

    public string? AmmoDescription => Ability.AmmoDescription;

    /// <summary>
    /// Whether to render <see cref="AmmoDescription"/> as a standalone line. The single-keyword
    /// case (96% of ammo abilities) folds AmmoDescription into the chip label, so this is false
    /// there — no duplication of "Beginner's Dense Arrow" between description prose, standalone
    /// ammo line, and chip. The multi-keyword case (~48 abilities) keeps the standalone line
    /// when present and not already contained in <see cref="Description"/>: AmmoDescription
    /// carries the OR-substitution context ("Simple Throwing Knife (or Crystal Ice x2)") that
    /// the individual ItemKeyword chips can't.
    /// </summary>
    public bool ShowAmmoDescription =>
        !string.IsNullOrEmpty(AmmoDescription)
        && AmmoKeywordRows.Count > 1
        && (string.IsNullOrEmpty(Description) || Description!.IndexOf(AmmoDescription!, StringComparison.Ordinal) < 0);

    public double? AmmoStickChance => Ability.AmmoStickChance;
    public double? AmmoConsumeChance => Ability.AmmoConsumeChance;
    public bool HasAmmoSection =>
        !string.IsNullOrEmpty(AmmoDescription)
        || AmmoKeywordRows.Count > 0
        || AmmoStickChance is > 0
        || AmmoConsumeChance is > 0;

    /// <summary>
    /// Command invoked when the user clicks a cross-link chip. Wired by
    /// <see cref="AbilitiesTabViewModel"/> to the navigator.
    /// </summary>
    public ICommand? OpenEntityCommand { get; }

    // ── Phase 5 grammar-primitive carriers ──────────────────────────────────

    /// <summary>Header skill/rank/group badge boxes collapsed to ONE inert Fact
    /// strip (pilot StatStrip pattern). The Rank box was the named G-b
    /// "Ability Rank" gold (T11) — gone by construction (FactTableVm has no brush).</summary>
    public FactTableVm HeaderStrip { get; }

    /// <summary>Core-mechanics label-value facts (Target · Reset time · Combat
    /// refresh) as one inert Strip; empties skipped, self-hides when empty.</summary>
    public FactTableVm CoreMechanicsStrip { get; }

    /// <summary>Currency costs as an inert <see cref="FactTableLayout.Grid"/>
    /// (Currency → Price). Self-hides when empty.</summary>
    public FactTableVm CostFact { get; }

    /// <summary>PvE stats as an inert label→value Grid (self-hides empty).</summary>
    public FactTableVm PvEStatsFact { get; }

    /// <summary>Special values as an inert label→value Grid (self-hides empty).</summary>
    public FactTableVm SpecialValuesFact { get; }

    /// <summary>Environmental flags as an inert label→value Grid.</summary>
    public FactTableVm EnvironmentalFact { get; }

    /// <summary>Special-targeting flags as an inert label→value Grid.</summary>
    public FactTableVm SpecialTargetingFact { get; }

    /// <summary>Pet flags as an inert label→value Grid.</summary>
    public FactTableVm PetFact { get; }

    /// <summary>Prerequisite ability as an inline Prose <see cref="LinkVm"/>; null when none.</summary>
    public LinkVm? PrerequisiteLink { get; }

    /// <summary>"Upgrade of" ability as an inline Prose <see cref="LinkVm"/>; null when none.</summary>
    public LinkVm? UpgradeOfLink { get; }

    /// <summary>"Shares reset timer with" ability as an inline Prose <see cref="LinkVm"/>.</summary>
    public LinkVm? SharesResetTimerWithLink { get; }

    /// <summary>Sibling abilities (same group) as <see cref="LinkVm"/> (Density="List").</summary>
    public IReadOnlyList<LinkVm> AbilitiesInGroupLinks { get; }

    /// <summary>Abilities upgrading from this one as <see cref="LinkVm"/> (Density="List").</summary>
    public IReadOnlyList<LinkVm> UpgradesToLinks { get; }

    /// <summary>Trainer-NPC sources as <see cref="LinkVm"/> (Density="List"; E6(a)
    /// — the "source/taught-by" Link, now the unified primitive).</summary>
    public IReadOnlyList<LinkVm> SourceLinks { get; }

    /// <summary>Required item-keyword filters as tag-form Set-references
    /// (ratified E4 — keyword filters, NOT Link); unwired ⇒ availability corollary.</summary>
    public IReadOnlyList<AbilityFilterSetRefVm> ItemKeywordReqSetRefs { get; }

    /// <summary>Required effect-keyword filters as tag-form Set-references (E4).</summary>
    public IReadOnlyList<AbilityFilterSetRefVm> EffectKeywordReqSetRefs { get; }

    /// <summary>Target effect-keyword filter as a single tag-form Set-ref (E4); null when unset.</summary>
    public AbilityFilterSetRefVm? TargetEffectKeywordReqSetRef { get; }

    /// <summary>Conditional-keyword rows reshaped so the EffectKeyword chip is a
    /// tag-form Set-ref (E4); the Condition prefix is Structure, the applied
    /// keyword inert Fact.</summary>
    public IReadOnlyList<AbilityConditionalRowVm> ConditionalRowVms { get; }

    /// <summary>Ammo rows reshaped: the ItemByKeyword chip is a tag-form Set-ref
    /// (E4); the <c>× Count</c> is adjacent inert Fact (carry-forward #1 —
    /// quantity is never part of the ref).</summary>
    public IReadOnlyList<AbilityAmmoRowVm> AmmoRowVms { get; }

    /// <summary>
    /// Footer identifier strip (matrix #14 / G-a · ratified E5). The Ability
    /// InternalName is a cross-entity reference key (abilities prereq/upgrade
    /// each other; NPCs teach by it) ⇒ the copyable <c>KEY</c> (Area's path),
    /// not an inert envelope <c>ROW</c>. <see cref="FactFooterVm.None"/> if keyless.
    /// </summary>
    public FactFooterVm Footer { get; }

    // ─── Helpers ──────────────────────────────────────────────────────────────────────

    private static string? ResolveSkillDisplay(IReferenceDataService refData, string? skillKey)
    {
        if (string.IsNullOrEmpty(skillKey)) return null;
        if (refData.Skills.TryGetValue(skillKey, out var s) && !string.IsNullOrEmpty(s.DisplayName))
            return s.DisplayName;
        return skillKey;
    }

    private static string? ResolveAbilityGroupDisplay(Ability ability) =>
        !string.IsNullOrEmpty(ability.AbilityGroupName)
            ? ability.AbilityGroupName
            : ability.AbilityGroup;

    private static EntityChipVm? BuildAbilityChip(
        string? internalName,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(internalName)) return null;
        var reference = EntityRef.Ability(internalName!);
        return new EntityChipVm(
            DisplayName: resolver.Resolve(reference),
            IconId: 0,
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference));
    }

    /// <summary>
    /// Split <see cref="Ability.ItemKeywordReqs"/> into real item-keyword chips and form-gate
    /// labels. Form-gate values (e.g. <c>"form:Cow"</c>, <c>"Werewolf"</c>, <c>"Beast"</c>)
    /// are caster-state requirements packed into the item-keyword array; routing them through
    /// the Items tab produces dead-link chips, so they peel off into <see cref="FormGateLabels"/>.
    /// <para>
    /// Corpus invariants (audited 2026-05-14, see #303): no ability mixes a form-gate with a
    /// real item keyword; no ability carries more than one form-gate value;
    /// <see cref="Ability.ItemKeywordReqErrorMessage"/> is always populated for gate-bearing
    /// abilities — so the player-facing message is the cleanest label source.
    /// </para>
    /// </summary>
    private static (IReadOnlyList<EntityChipVm> Chips, IReadOnlyList<string> FormGateLabels) PartitionItemKeywordReqs(
        IReadOnlyList<string>? itemKeywordReqs,
        string? errorMessage,
        IReferenceNavigator navigator)
    {
        if (itemKeywordReqs is null || itemKeywordReqs.Count == 0) return ([], []);
        var chips = new List<EntityChipVm>(itemKeywordReqs.Count);
        var gateValues = new List<string>();
        foreach (var kw in itemKeywordReqs)
        {
            if (string.IsNullOrEmpty(kw)) continue;
            if (IsFormGateKeyword(kw))
            {
                gateValues.Add(kw);
                continue;
            }
            // #327: single-keyword Items filter pivot (1:1 per the #318 chip-vs-popup
            // rule — one tag → "open the Items tab filtered to this keyword"). Restored
            // via EntityKind.ItemByKeyword (the symmetric Items-side twin of EffectKeyword),
            // NOT the retired #270 ItemKeyword recipe-slot fan-out kind. #326 had degraded
            // this to non-navigable plain text when the double-duty ItemKeyword kind was
            // retired for its fan-out use. NOTE: #332 separately owes partitioning the
            // HasHands/Unarmed hand-state pseudo-keywords out of this array — they match
            // zero items so they pivot to an empty list. That pre-existing data-
            // classification defect is intentionally NOT fixed here (#327 is navigation-
            // mechanism only); restoring navigability re-exposes it as expected.
            var reference = EntityRef.ItemByKeyword(kw);
            chips.Add(new EntityChipVm(
                DisplayName: kw,
                IconId: 0,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference)));
        }

        if (gateValues.Count == 0) return (chips, []);

        // Prefer the player-facing error message ("Must be in Cow Form"). Fallback for data
        // without one: derive from the raw gate value ("form:Cow" → "Cow Form", "Werewolf" →
        // "Werewolf Form").
        var labels = new List<string>(1);
        if (!string.IsNullOrEmpty(errorMessage))
        {
            labels.Add(errorMessage!);
        }
        else
        {
            foreach (var gv in gateValues)
                labels.Add(DeriveFormGateLabel(gv));
        }
        return (chips, labels);
    }

    private static bool IsFormGateKeyword(string keyword) =>
        keyword.StartsWith("form:", StringComparison.Ordinal)
        || keyword == "Werewolf"
        || keyword == "Beast";

    private static string DeriveFormGateLabel(string gateValue) =>
        gateValue.StartsWith("form:", StringComparison.Ordinal)
            ? gateValue["form:".Length..] + " Form"
            : gateValue + " Form";

    private static IReadOnlyList<EntityChipVm> BuildEffectKeywordChips(
        IReadOnlyList<string>? effectKeywordReqs,
        IReferenceNavigator navigator)
    {
        if (effectKeywordReqs is null || effectKeywordReqs.Count == 0) return [];
        var list = new List<EntityChipVm>(effectKeywordReqs.Count);
        foreach (var kw in effectKeywordReqs)
        {
            var chip = BuildSingleEffectKeywordChip(kw, navigator);
            if (chip is not null) list.Add(chip);
        }
        return list;
    }

    private static EntityChipVm? BuildSingleEffectKeywordChip(string? keyword, IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(keyword)) return null;
        var reference = EntityRef.EffectKeyword(keyword!);
        return new EntityChipVm(
            DisplayName: keyword!,
            IconId: 0,
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference));
    }

    private static IReadOnlyList<string> BuildSpecialCasterRequirementLabels(
        IReadOnlyList<AbilitySpecialCasterRequirement>? requirements)
    {
        if (requirements is null || requirements.Count == 0) return [];
        var list = new List<string>(requirements.Count);
        foreach (var r in requirements)
        {
            list.Add(FormatSpecialCasterRequirement(r));
        }
        return list;
    }

    private static string FormatSpecialCasterRequirement(AbilitySpecialCasterRequirement r) => r switch
    {
        EffectKeywordUnsetAbilityRequirement e =>
            string.IsNullOrEmpty(e.Keyword) ? "Effect keyword unset" : $"Effect keyword unset: {e.Keyword}",
        HasEffectKeywordAbilityRequirement e =>
            string.IsNullOrEmpty(e.Keyword) ? "Has effect keyword" : $"Has effect keyword: {e.Keyword}",
        EquippedItemKeywordAbilityRequirement e =>
            FormatEquippedItemKeyword(e),
        InventoryItemKeywordRequirement e =>
            string.IsNullOrEmpty(e.Keyword) ? "Inventory item keyword" : $"Inventory item: {e.Keyword}",
        HasInventorySpaceForRequirement e =>
            string.IsNullOrEmpty(e.Item) ? "Has inventory space" : $"Has inventory space for: {e.Item}",
        InHotspotAbilityRequirement e =>
            string.IsNullOrEmpty(e.Name) ? "In hotspot" : $"In hotspot: {e.Name}",
        IsNotInHotspotRequirement e =>
            string.IsNullOrEmpty(e.Name) ? "Not in hotspot" : $"Not in hotspot: {e.Name}",
        InteractionFlagSetAbilityRequirement e =>
            string.IsNullOrEmpty(e.InteractionFlag) ? "Interaction flag set" : $"Interaction flag set: {e.InteractionFlag}",
        InMusicPerformanceRequirement => "In music performance",
        IsDancingOnPoleRequirement => "Dancing on pole",
        IsHardcoreAbilityRequirement => "Hardcore",
        IsLongtimeAnimalAbilityRequirement => "Longtime animal",
        IsNotGuestAbilityRequirement => "Not a guest",
        IsNotInCombatRequirement => "Not in combat",
        IsVampireAbilityRequirement => "Vampire",
        IsVegetarianRequirement => "Vegetarian",
        IsVolunteerGuideRequirement => "Volunteer guide",
        UnknownAbilitySpecialCasterRequirement u =>
            string.IsNullOrEmpty(u.DiscriminatorValue) ? $"Unknown requirement ({r.T})" : $"Unknown requirement ({u.DiscriminatorValue})",
        _ => string.IsNullOrEmpty(r.T) ? "Requirement" : r.T,
    };

    private static string FormatEquippedItemKeyword(EquippedItemKeywordAbilityRequirement e)
    {
        var keyword = e.Keyword ?? "item";
        var parts = new List<string>(2);
        if (e.MinCount is int min) parts.Add($"min {min}");
        if (e.MaxCount is int max) parts.Add($"max {max}");
        return parts.Count == 0
            ? $"Equipped item: {keyword}"
            : $"Equipped item: {keyword} ({string.Join(", ", parts)})";
    }

    private static IReadOnlyList<AbilityCostRow> BuildCostRows(IReadOnlyList<AbilityCost>? costs)
    {
        if (costs is null || costs.Count == 0) return [];
        var list = new List<AbilityCostRow>(costs.Count);
        foreach (var c in costs)
        {
            if (c is null) continue;
            list.Add(new AbilityCostRow(c.Currency ?? "(unknown)", c.Price));
        }
        return list;
    }

    private static IReadOnlyList<AbilityConditionalKeywordRow> BuildConditionalKeywordRows(
        IReadOnlyList<AbilityConditionalKeyword>? conditionals,
        IReferenceNavigator navigator)
    {
        if (conditionals is null || conditionals.Count == 0) return [];
        var list = new List<AbilityConditionalKeywordRow>(conditionals.Count);
        foreach (var c in conditionals)
        {
            if (c is null) continue;
            string condition;
            EntityChipVm? chip = null;
            if (c.Default == true)
            {
                condition = "Default";
            }
            else if (!string.IsNullOrEmpty(c.EffectKeywordMustExist))
            {
                condition = "When effect keyword present:";
                chip = BuildSingleEffectKeywordChip(c.EffectKeywordMustExist, navigator);
            }
            else if (!string.IsNullOrEmpty(c.EffectKeywordMustNotExist))
            {
                condition = "When effect keyword absent:";
                chip = BuildSingleEffectKeywordChip(c.EffectKeywordMustNotExist, navigator);
            }
            else
            {
                condition = "Always";
            }
            list.Add(new AbilityConditionalKeywordRow(
                Keyword: c.Keyword ?? "(none)",
                Condition: condition,
                EffectKeywordChip: chip));
        }
        return list;
    }

    /// <summary>
    /// Build ammo-keyword chip rows. Each row holds an <see cref="EntityChipVm"/> keyed by
    /// <see cref="EntityRef.ItemByKeyword(string)"/> — not <see cref="EntityRef.Item(string)"/> —
    /// because the JSON field (<c>"DenseArrow1"</c>, <c>"SporeBomb1"</c>) is a *keyword* that
    /// matches multiple ammo items at that tier, so the chip opens the Items tab filtered by
    /// <c>Keywords CONTAINS</c> rather than landing on a single item.
    /// <para>
    /// When the ability has exactly one ammo keyword and a non-empty <see cref="Ability.AmmoDescription"/>,
    /// the chip's display label folds in the friendly description (e.g. <c>"Beginner's Dense Arrow"</c>
    /// instead of <c>"DenseArrow1"</c>). This collapses the duplicated "Uses a Beginner's Dense Arrow"
    /// line that PG bakes into ~76% of ammo-using ability descriptions. Multi-keyword abilities
    /// keep their raw <see cref="AbilityAmmoKeyword.ItemKeyword"/> as the chip label since the single
    /// shared AmmoDescription text would be ambiguous across rows; their AmmoDescription surfaces
    /// via <see cref="ShowAmmoDescription"/> instead.
    /// </para>
    /// </summary>
    private static IReadOnlyList<AbilityAmmoKeywordRow> BuildAmmoKeywordRows(
        IReadOnlyList<AbilityAmmoKeyword>? ammoKeywords,
        string? ammoDescription,
        IReferenceNavigator navigator)
    {
        if (ammoKeywords is null || ammoKeywords.Count == 0) return [];
        var useDescriptionAsLabel = ammoKeywords.Count == 1 && !string.IsNullOrEmpty(ammoDescription);
        var list = new List<AbilityAmmoKeywordRow>(ammoKeywords.Count);
        foreach (var a in ammoKeywords)
        {
            if (a is null) continue;
            var keyword = a.ItemKeyword ?? "(any)";
            // #327: ammo-keyword chip is a single-keyword Items filter pivot (1:1 per the
            // #318 chip-vs-popup rule — the JSON field is a *keyword* matching multiple
            // ammo items at a tier, so the chip opens the Items tab filtered by
            // Keywords CONTAINS rather than landing on one item). Restored via the
            // symmetric EntityKind.ItemByKeyword (NOT the retired #270 ItemKeyword fan-out
            // kind); #326 had degraded it to non-navigable plain text.
            var reference = EntityRef.ItemByKeyword(keyword);
            var chip = new EntityChipVm(
                DisplayName: useDescriptionAsLabel ? ammoDescription! : keyword,
                IconId: 0,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference));
            list.Add(new AbilityAmmoKeywordRow(Chip: chip, Count: a.Count));
        }
        return list;
    }

    /// <summary>
    /// Build the environmental-flags strip. Only rows whose value is "notable" — i.e. NOT the
    /// universal default — actually surface to the XAML. Per the cookbook's
    /// <em>Default-value noise filtering</em> rule: a chip that says the same thing on 90%
    /// of entities is pure noise. Concretely: most abilities don't work underwater/falling/
    /// stunned/mounted, so <c>=true</c> is notable. Most abilities DO work in combat, so
    /// <c>=false</c> is notable.
    /// </summary>
    private static IReadOnlyList<AbilityFlagRow> BuildEnvironmentalFlags(Ability ability)
    {
        var list = new List<AbilityFlagRow>();
        AddIf(list, "Works underwater", ability.WorksUnderwater is true);
        AddIf(list, "Works while falling", ability.WorksWhileFalling is true);
        AddIf(list, "Works while stunned", ability.WorksWhileStunned is true);
        AddIf(list, "Works while mounted", ability.WorksWhileMounted is true);
        // Most abilities are in-combat-capable; flag the rare ones that aren't.
        AddIf(list, "Cannot be used in combat", ability.WorksInCombat is false);
        AddIf(list, "Suppresses monster shout", ability.CanSuppressMonsterShout is true);
        return list;
    }

    private static IReadOnlyList<AbilityFlagRow> BuildSpecialTargetingFlags(Ability ability)
    {
        var list = new List<AbilityFlagRow>();
        // TargetEffectKeywordReq is surfaced as a chip via TargetEffectKeywordReqChip above the
        // flag rows, not in this list — the chip carries the EffectKeyword deep-link affordance.
        if (!string.IsNullOrEmpty(ability.TargetTypeTagReq))
            list.Add(new AbilityFlagRow("Target type tag", ability.TargetTypeTagReq!));
        if (ability.SpecialTargetingTypeReq is int s)
            list.Add(new AbilityFlagRow("Special targeting type", s.ToString()));
        if (ability.AoEIsCenteredOnCaster is true)
            list.Add(new AbilityFlagRow("AoE centered on caster", "Yes"));
        if (ability.CanTargetUntargetableEnemies is true)
            list.Add(new AbilityFlagRow("Can target untargetable enemies", "Yes"));
        return list;
    }

    private static IReadOnlyList<AbilityFlagRow> BuildPetFlags(Ability ability)
    {
        var list = new List<AbilityFlagRow>();
        if (!string.IsNullOrEmpty(ability.PetTypeTagReq))
            list.Add(new AbilityFlagRow("Pet type tag", ability.PetTypeTagReq!));
        if (ability.PetTypeTagReqMax is int m)
            list.Add(new AbilityFlagRow("Pet type tag (max)", m.ToString()));
        if (ability.IsCosmeticPet is true)
            list.Add(new AbilityFlagRow("Cosmetic pet", "Yes"));
        return list;
    }

    private static IReadOnlyList<AbilityFlagRow> BuildInternalFlags(Ability ability)
    {
        var list = new List<AbilityFlagRow>();
        if (ability.InternalAbility is true)
            list.Add(new AbilityFlagRow("Internal ability", "Yes"));
        if (!string.IsNullOrEmpty(ability.SpecialInfo))
            list.Add(new AbilityFlagRow("Special info", ability.SpecialInfo!));
        if (ability.CanBeOnSidebar is false)
            list.Add(new AbilityFlagRow("Cannot be on sidebar", "Yes"));
        if (ability.IsHarmless is true)
            list.Add(new AbilityFlagRow("Harmless", "Yes"));
        if (ability.IsTimerResetWhenDisabling is true)
            list.Add(new AbilityFlagRow("Timer resets on disable", "Yes"));
        if (ability.IgnoreEffectErrors is true)
            list.Add(new AbilityFlagRow("Ignores effect errors", "Yes"));
        return list;
    }

    private static IReadOnlyList<AbilityFlagRow> BuildPvEStats(AbilityPvE? pve)
    {
        if (pve is null) return [];
        var list = new List<AbilityFlagRow>();
        if (pve.Damage is int dmg && dmg > 0)
            list.Add(new AbilityFlagRow("Damage", dmg.ToString()));
        if (pve.Range > 0)
            list.Add(new AbilityFlagRow("Range", pve.Range.ToString()));
        if (pve.PowerCost > 0)
            list.Add(new AbilityFlagRow("Power cost", pve.PowerCost.ToString()));
        if (pve.AoE is double aoe && aoe > 0)
            list.Add(new AbilityFlagRow("AoE", aoe.ToString("0.##")));
        if (pve.RageCost is int rc && rc != 0)
            list.Add(new AbilityFlagRow("Rage cost", rc.ToString()));
        if (pve.RageBoost is int rb && rb != 0)
            list.Add(new AbilityFlagRow("Rage boost", rb.ToString()));
        if (pve.RageMultiplier is int rm && rm != 0)
            list.Add(new AbilityFlagRow("Rage multiplier", rm.ToString()));
        if (pve.CritDamageMod is double cdm && cdm != 0)
            list.Add(new AbilityFlagRow("Crit damage mod", cdm.ToString("0.##")));
        if (pve.Accuracy is double acc && acc != 0)
            list.Add(new AbilityFlagRow("Accuracy", acc.ToString("0.##")));
        if (pve.TauntDelta is int td && td != 0)
            list.Add(new AbilityFlagRow("Taunt delta", td.ToString()));
        if (pve.TempTauntDelta is int ttd && ttd != 0)
            list.Add(new AbilityFlagRow("Temp taunt delta", ttd.ToString()));
        if (pve.HealthSpecificDamage is int hsd && hsd != 0)
            list.Add(new AbilityFlagRow("Health-specific damage", hsd.ToString()));
        if (pve.ArmorSpecificDamage is int asd && asd != 0)
            list.Add(new AbilityFlagRow("Armor-specific damage", asd.ToString()));
        if (pve.ArmorMitigationRatio is int amr && amr != 0)
            list.Add(new AbilityFlagRow("Armor mitigation ratio", amr.ToString()));
        if (pve.ExtraDamageIfTargetVulnerable is int edv && edv != 0)
            list.Add(new AbilityFlagRow("Extra damage if target vulnerable", edv.ToString()));
        return list;
    }

    private static IReadOnlyList<AbilitySpecialValueRow> BuildSpecialValueRows(AbilityPvE? pve)
    {
        if (pve?.SpecialValues is null || pve.SpecialValues.Count == 0) return [];
        var list = new List<AbilitySpecialValueRow>(pve.SpecialValues.Count);
        foreach (var sv in pve.SpecialValues)
        {
            if (sv is null) continue;
            if (sv.SkipIfZero == true && sv.Value == 0) continue;
            list.Add(new AbilitySpecialValueRow(
                Label: sv.Label ?? "(value)",
                Value: sv.Value,
                Suffix: sv.Suffix));
        }
        return list;
    }

    private static IReadOnlyList<AbilityDoTRow> BuildDoTRows(AbilityPvE? pve)
    {
        if (pve?.DoTs is null || pve.DoTs.Count == 0) return [];
        var list = new List<AbilityDoTRow>(pve.DoTs.Count);
        foreach (var d in pve.DoTs)
        {
            if (d is null) continue;
            list.Add(new AbilityDoTRow(
                DamagePerTick: d.DamagePerTick,
                DamageType: d.DamageType ?? "(unspecified)",
                NumTicks: d.NumTicks,
                Duration: d.Duration,
                Preface: d.Preface));
        }
        return list;
    }

    private static IReadOnlyList<EntityChipVm> BuildAbilitiesInGroupChips(
        Ability ability,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(ability.AbilityGroup)) return [];
        if (!refData.AbilitiesInGroup.TryGetValue(ability.AbilityGroup!, out var siblings) || siblings.Count == 0)
            return [];
        return siblings
            .Where(a => !string.IsNullOrEmpty(a.InternalName)
                && !string.Equals(a.InternalName, ability.InternalName, StringComparison.Ordinal))
            .OrderBy(a => a.Level)
            .ThenBy(a => a.Name ?? a.InternalName, StringComparer.OrdinalIgnoreCase)
            .Select(a => BuildResolvedAbilityChip(a.InternalName!, resolver, navigator, a.IconID))
            .ToList();
    }

    private static IReadOnlyList<EntityChipVm> BuildUpgradesToChips(
        Ability ability,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(ability.InternalName)) return [];
        if (!refData.AbilitiesUpgradingFrom.TryGetValue(ability.InternalName!, out var upgrades) || upgrades.Count == 0)
            return [];
        return upgrades
            .Where(a => !string.IsNullOrEmpty(a.InternalName))
            .OrderBy(a => a.Level)
            .ThenBy(a => a.Name ?? a.InternalName, StringComparer.OrdinalIgnoreCase)
            .Select(a => BuildResolvedAbilityChip(a.InternalName!, resolver, navigator, a.IconID))
            .ToList();
    }

    private static EntityChipVm BuildResolvedAbilityChip(
        string internalName,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator,
        int iconId)
    {
        var reference = EntityRef.Ability(internalName);
        return new EntityChipVm(
            DisplayName: resolver.Resolve(reference),
            IconId: iconId,
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference));
    }

    private IReadOnlyList<EntityChipVm> BuildSourceChips(
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(Ability.InternalName)) return [];
        if (!refData.AbilitySources.TryGetValue(Ability.InternalName!, out var sources) || sources.Count == 0)
            return [];

        // Today the only entity-shaped ability source is Training (NPC trainer). Other source
        // types (Skill / Item / etc.) surface elsewhere — Skill via the ability's Skill chip in
        // the header, Item via the item's "Used in" cross-link section. Leaving non-NPC source
        // types out of the chip strip keeps it scannable and free of plain-text noise.
        var list = new List<EntityChipVm>();
        foreach (var s in sources)
        {
            if (!string.Equals(s.Type, "Training", StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(s.Npc)) continue;
            var reference = EntityRef.Npc(s.Npc!);
            list.Add(new EntityChipVm(
                DisplayName: resolver.Resolve(reference),
                IconId: 0,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference)));
        }
        return list;
    }

    private static void AddIf(List<AbilityFlagRow> rows, string label, bool condition)
    {
        if (condition) rows.Add(new AbilityFlagRow(label, "Yes"));
    }
}

/// <summary>
/// A Set-reference carrier (the pilot <c>RecipeKeywordSlotVm</c> idiom): the
/// <see cref="SetRefVm"/> the shared <c>SetRef</c> binds plus the per-chip
/// <see cref="Activate"/> bridging <c>SetRef.ActivateCommand</c> (which passes
/// the VM) to the host <c>OpenEntityCommand(reference)</c>. <see cref="Activate"/>
/// is null for an unwired tag (availability corollary — still the blue chassis;
/// the click is a safe no-op).
/// </summary>
public sealed class AbilityFilterSetRefVm
{
    public AbilityFilterSetRefVm(SetRefVm setRef, ICommand? activate)
    {
        SetRef = setRef;
        Activate = activate;
    }

    public SetRefVm SetRef { get; }
    public ICommand? Activate { get; }
}

/// <summary>
/// View-side reshape of an <see cref="AbilityConditionalKeywordRow"/>: the
/// trigger <see cref="Condition"/> prefix (Structure), the optional
/// <see cref="EffectKeywordSetRef"/> (tag-form Set-ref — ratified E4), and the
/// applied <see cref="Keyword"/> (inert Fact). The legacy
/// <c>AbilityConditionalKeywordRow</c> is retained for the tests; this wraps it.
/// </summary>
public sealed class AbilityConditionalRowVm
{
    public AbilityConditionalRowVm(string condition, string keyword, AbilityFilterSetRefVm? effectKeywordSetRef)
    {
        Condition = condition;
        Keyword = keyword;
        EffectKeywordSetRef = effectKeywordSetRef;
    }

    public string Condition { get; }
    public string Keyword { get; }
    public AbilityFilterSetRefVm? EffectKeywordSetRef { get; }
}

/// <summary>View-side reshape of an <see cref="AbilityAmmoKeywordRow"/>: the
/// ItemByKeyword chip as a tag-form Set-ref (E4) plus the adjacent
/// <see cref="Count"/> (inert Fact — carry-forward #1).</summary>
public sealed class AbilityAmmoRowVm
{
    public AbilityAmmoRowVm(AbilityFilterSetRefVm setRef, int count)
    {
        SetRef = setRef;
        Count = count;
    }

    public AbilityFilterSetRefVm SetRef { get; }
    public int Count { get; }
}

/// <summary>One row in <see cref="AbilityDetailViewModel.CostRows"/>.</summary>
public sealed record AbilityCostRow(string Currency, int Price);

/// <summary>
/// One row in <see cref="AbilityDetailViewModel.ConditionalKeywordRows"/>. The
/// <paramref name="Condition"/> string carries the human-readable trigger prefix
/// ("When effect keyword present:", "When effect keyword absent:", "Always",
/// "Default"); when an effect-keyword chip is appropriate it lands on
/// <paramref name="EffectKeywordChip"/> for the XAML to render alongside the
/// prefix. The chip is null for unconditional / default rows.
/// </summary>
public sealed record AbilityConditionalKeywordRow(string Keyword, string Condition, EntityChipVm? EffectKeywordChip);

/// <summary>One row in <see cref="AbilityDetailViewModel.AmmoKeywordRows"/>. The chip is keyed by
/// <see cref="EntityRef.ItemKeyword(string)"/>; clicking opens the Items tab filtered by that keyword.</summary>
public sealed record AbilityAmmoKeywordRow(EntityChipVm Chip, int Count);

/// <summary>Generic label-value row used by environmental / pet / internal / PvE-stat blocks.</summary>
public sealed record AbilityFlagRow(string Label, string Value);

/// <summary>One row in <see cref="AbilityDetailViewModel.SpecialValueRows"/>.</summary>
public sealed record AbilitySpecialValueRow(string Label, double Value, string? Suffix)
{
    public string Display => string.IsNullOrEmpty(Suffix)
        ? Value.ToString("0.##")
        : $"{Value.ToString("0.##")} {Suffix}";
}

/// <summary>One row in <see cref="AbilityDetailViewModel.DoTRows"/>.</summary>
public sealed record AbilityDoTRow(int DamagePerTick, string DamageType, int NumTicks, int Duration, string? Preface)
{
    public string Display =>
        $"{DamagePerTick} {DamageType} every {(NumTicks > 0 && Duration > 0 ? (Duration / (double)NumTicks).ToString("0.##") : "?")}s × {NumTicks} ({Duration}s total)";
}
