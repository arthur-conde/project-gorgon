using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Mithril.Reference.Models.Quests;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Read-only projection of a <see cref="Quest"/> for the Silmarillion Quests tab detail pane.
/// Hostable in both the master-detail right pane and the popup <see cref="Silmarillion.Views.QuestDetailWindow"/>.
/// <para>
/// All polymorphic content — requirement groups, reward groups, objective rows — is bucketed
/// by player intent in the constructor via <see cref="QuestDetailProjector"/>. Internal-name
/// references inside each subclass are resolved to display names there too, so the XAML
/// renders verbatim what the projector produces.
/// </para>
/// <para>
/// Cross-link chips (giver/turn-in NPC, reward items / recipes, follow-up quests) are
/// built here so the view-model owns the <c>_navigator.CanOpen</c> calls — chips degrade
/// to plain text automatically when their target kind isn't tabbed yet, per the cookbook's
/// "let CanOpen decide" rule.
/// </para>
/// </summary>
public sealed class QuestDetailViewModel
{
    public QuestDetailViewModel(
        Quest quest,
        string internalName,
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver,
        ICommand? openEntityCommand = null)
    {
        Quest = quest;
        InternalName = internalName;
        DisplayName = nameResolver.Resolve(EntityRef.Quest(internalName));
        LocationDisplay = quest.DisplayedLocation;
        Level = quest.Level;
        IsCancellable = quest.IsCancellable ?? false;
        IsGuildQuest = quest.IsGuildQuest ?? false;
        IsWorkOrder = !string.IsNullOrEmpty(quest.WorkOrderSkill);
        WorkOrderSkillDisplay = string.IsNullOrEmpty(quest.WorkOrderSkill)
            ? null
            : ResolveSkillDisplay(refData, quest.WorkOrderSkill!);

        GiverChip = BuildNpcChip(quest.QuestNpc, refData, nameResolver, navigator);
        TurnInChip = BuildNpcChip(quest.MainNpcName, refData, nameResolver, navigator);
        FavorNpcChip = BuildNpcChip(quest.FavorNpc, refData, nameResolver, navigator);

        Objectives = QuestDetailProjector.BuildObjectives(quest, refData, nameResolver, navigator);
        RequirementGroups = QuestDetailProjector.BuildRequirementGroups(quest.Requirements, refData, nameResolver, navigator);
        SustainRequirementGroups = QuestDetailProjector.BuildRequirementGroups(quest.RequirementsToSustain, refData, nameResolver, navigator);

        RewardGroups = QuestDetailProjector.BuildRewardGroups(quest, refData, nameResolver, navigator);
        RewardItemChips = BuildItemChips(quest.Rewards_Items, refData, nameResolver, navigator);
        RewardRecipeChips = BuildRecipeRewardChips(quest, refData, nameResolver, navigator);
        FollowUpQuestChips = BuildQuestChips(quest.FollowUpQuests, nameResolver, navigator);

        PreGiveItemChips = BuildItemChips(quest.PreGiveItems, refData, nameResolver, navigator);
        PreGiveRecipeChips = BuildRecipeChips(quest.PreGiveRecipes, refData, nameResolver, navigator);

        RepeatabilityChip = QuestCadenceClassifier.BadgeText(quest);
        RepeatabilityDetail = QuestCadenceClassifier.DetailText(quest);
        FavorRewardDisplay = BuildFavorRewardDisplay(quest, refData, nameResolver);
        BadgesDisplay = BuildBadgesDisplay(IsCancellable, IsGuildQuest, IsWorkOrder, WorkOrderSkillDisplay);

        OpenEntityCommand = openEntityCommand;

        // ── Phase 5 grammar-primitive projections ──────────────────────────────
        // Legacy chip/group/string members above stay (the existing tests + the
        // detail-pane contract); these are the grammar-tier carriers the view
        // binds. Quest is the ORIGIN of the dual-shape row the Recipe pilot was
        // modelled on ("Dual-shape, mirroring QuestDetailView"), so the
        // QuestRequirementDisplay / QuestRewardDisplay rows wrap to LinkVm
        // EXACTLY via the pilot's RecipeRequirementRowVm idiom (prose row ⇒
        // Link null; chip row ⇒ Structure prefix + Prose Link). The
        // *Display/*Group/*ObjectiveRow records live in their own files (out
        // of this view's diff-guard scope) so they are WRAPPED, not edited —
        // and the bespoke RequirementChipVmConverter the old XAML used to coerce
        // a row → EntityChip is no longer referenced (the wrapper builds the
        // LinkVm directly; the converter class is now dead — flagged as a
        // follow-up cleanup, out of this PR's 3-file scope).
        // #404 Phase-2: giver/turn-in + reward/pre-give/follow-up chips = 1:1
        // refs = Link; the gold group labels + RepeatabilityChip (T11) + the
        // named G-b "Quest favor line" = inert Fact (gold dropped); the
        // dialogue Expander = Control (ratified E3 — correctly classified,
        // left as-is); InternalName footer = a cross-entity reference key
        // (follow-ups / NPCs / vaults resolve a quest by it) ⇒ copyable KEY.

        GiverLink = GiverChip is null ? null : LinkVm.From(GiverChip);
        TurnInLink = TurnInChip is null ? null : LinkVm.From(TurnInChip);

        RewardItemLinks = RewardItemChips.Select(LinkVm.From).ToList();
        RewardRecipeLinks = RewardRecipeChips.Select(LinkVm.From).ToList();
        FollowUpQuestLinks = FollowUpQuestChips.Select(LinkVm.From).ToList();
        PreGiveItemLinks = PreGiveItemChips.Select(LinkVm.From).ToList();
        PreGiveRecipeLinks = PreGiveRecipeChips.Select(LinkVm.From).ToList();

        RequirementGroupVms = RequirementGroups.Select(QuestRequirementGroupVm.From).ToList();
        SustainRequirementGroupVms = SustainRequirementGroups.Select(QuestRequirementGroupVm.From).ToList();
        RewardGroupVms = RewardGroups.Select(QuestRewardGroupVm.From).ToList();
        ObjectiveRowVms = Objectives.Select(QuestObjectiveRowVm.From).ToList();

        // Header Level / Location / Repeatability badge boxes collapse into ONE
        // inert Fact strip (the pilot StatStrip pattern). RepeatabilityChip was
        // the named G-b gold-tinted badge (T11) — gone by construction (no
        // brush on FactTableVm). The precise cooldown the old hover tooltip
        // recovered stays queryable via QuestListRow.ReuseMinutes (accepted
        // fidelity trade-off — consistency over fidelity, the ratified G4 bar).
        var hdr = new List<FactPair>(3);
        if (Level is { } lvl) hdr.Add(new FactPair(null, $"Level {lvl}"));
        if (!string.IsNullOrEmpty(LocationDisplay)) hdr.Add(new FactPair(null, LocationDisplay!));
        if (!string.IsNullOrEmpty(RepeatabilityChip)) hdr.Add(new FactPair(null, RepeatabilityChip!));
        HeaderStrip = FactTableVm.Strip(hdr);

        Footer = string.IsNullOrEmpty(InternalName)
            ? FactFooterVm.None()
            : FactFooterVm.Key(InternalName);
    }

    public Quest Quest { get; }
    public string InternalName { get; }
    public string DisplayName { get; }
    public int? Level { get; }
    public string? LocationDisplay { get; }
    public bool IsCancellable { get; }
    public bool IsGuildQuest { get; }
    public bool IsWorkOrder { get; }
    public string? WorkOrderSkillDisplay { get; }

    public EntityChipVm? GiverChip { get; }
    public EntityChipVm? TurnInChip { get; }
    public EntityChipVm? FavorNpcChip { get; }

    public IReadOnlyList<QuestObjectiveRow> Objectives { get; }
    public IReadOnlyList<QuestRequirementGroup> RequirementGroups { get; }
    public IReadOnlyList<QuestRequirementGroup> SustainRequirementGroups { get; }

    public IReadOnlyList<QuestRewardGroup> RewardGroups { get; }
    public IReadOnlyList<EntityChipVm> RewardItemChips { get; }
    public IReadOnlyList<EntityChipVm> RewardRecipeChips { get; }
    public IReadOnlyList<EntityChipVm> FollowUpQuestChips { get; }

    public IReadOnlyList<EntityChipVm> PreGiveItemChips { get; }
    public IReadOnlyList<EntityChipVm> PreGiveRecipeChips { get; }

    /// <summary>
    /// Short-form repeatability for a header badge: <c>"Daily"</c>, <c>"Weekly"</c>,
    /// <c>"Every 20h"</c>, etc. Null for one-time quests so the chip collapses entirely —
    /// most story quests are one-shot, and an explicit "One-time" chip on every one would
    /// be noise. Sourced from <see cref="QuestCadenceClassifier.BadgeText"/>; the precise
    /// interval is exposed for querying as <see cref="QuestListRow.ReuseMinutes"/>.
    /// </summary>
    public string? RepeatabilityChip { get; }

    /// <summary>
    /// Precise long-form interval ("Repeatable every 20 hours") shown as a tooltip on the
    /// friendly <see cref="RepeatabilityChip"/> badge, so hovering "Daily" recovers the
    /// exact cooldown the badge rounds. Null for one-time quests (badge collapsed).
    /// </summary>
    public string? RepeatabilityDetail { get; }

    public string? FavorRewardDisplay { get; }
    public string? BadgesDisplay { get; }

    public string? Description => Quest.Description;
    public string? PrefaceText => Quest.PrefaceText;
    public string? SuccessText => Quest.SuccessText;
    public string? MidwayText => Quest.MidwayText;

    /// <summary>
    /// <see cref="PrefaceText"/> with a bold "Preface: " prefix baked in, so a single
    /// <c>FormattedText.Text</c>-bound TextBlock can render the label + the body with both the
    /// label's bold and any embedded &lt;i&gt;/&lt;b&gt; markup the source carries. Null when
    /// the underlying field is absent, which drives the section's visibility binding.
    /// </summary>
    public string? PrefaceFormatted => FormatLabelled("Preface: ", PrefaceText);
    public string? MidwayFormatted => FormatLabelled("Midway: ", MidwayText);
    public string? SuccessFormatted => FormatLabelled("On completion: ", SuccessText);

    /// <summary>
    /// True when at least one of <see cref="PrefaceText"/> / <see cref="MidwayText"/> /
    /// <see cref="SuccessText"/> is present. Drives the flavor-text accordion's visibility
    /// — three independent NullOrEmptyToVis bindings on the body wouldn't collapse the
    /// expander when *all* three are missing.
    /// </summary>
    public bool HasFlavorText =>
        !string.IsNullOrEmpty(PrefaceText)
        || !string.IsNullOrEmpty(MidwayText)
        || !string.IsNullOrEmpty(SuccessText);

    private static string? FormatLabelled(string label, string? body) =>
        string.IsNullOrEmpty(body) ? null : $"<b>{label}</b>{body}";

    /// <summary>
    /// Command invoked when the user clicks a cross-link chip (item / recipe / quest / NPC).
    /// Receives the chip's <see cref="EntityRef"/>. Wired by <see cref="QuestsTabViewModel"/>
    /// to the navigator.
    /// </summary>
    public ICommand? OpenEntityCommand { get; }

    // ── Phase 5 grammar-primitive carriers ──────────────────────────────────

    /// <summary>Quest giver NPC as an inline Prose <see cref="LinkVm"/> (behind
    /// the Structure "Giver:" prefix). Null when none.</summary>
    public LinkVm? GiverLink { get; }

    /// <summary>Turn-in NPC as an inline Prose <see cref="LinkVm"/>. Null when none.</summary>
    public LinkVm? TurnInLink { get; }

    /// <summary>Reward-item cross-links as <see cref="LinkVm"/> (Density="List").</summary>
    public IReadOnlyList<LinkVm> RewardItemLinks { get; }

    /// <summary>Reward-recipe cross-links as <see cref="LinkVm"/> (Density="List").</summary>
    public IReadOnlyList<LinkVm> RewardRecipeLinks { get; }

    /// <summary>Follow-up quest cross-links as <see cref="LinkVm"/> (Density="List").</summary>
    public IReadOnlyList<LinkVm> FollowUpQuestLinks { get; }

    /// <summary>Pre-give item cross-links as <see cref="LinkVm"/> (Density="List").</summary>
    public IReadOnlyList<LinkVm> PreGiveItemLinks { get; }

    /// <summary>Pre-give recipe cross-links as <see cref="LinkVm"/> (Density="List").</summary>
    public IReadOnlyList<LinkVm> PreGiveRecipeLinks { get; }

    /// <summary>Requirement groups reshaped so each dual-shape row carries a
    /// <see cref="LinkVm"/> (chip row) or null (prose row) — the pilot
    /// <c>RecipeRequirementRowVm</c> idiom, applied to its origin.</summary>
    public IReadOnlyList<QuestRequirementGroupVm> RequirementGroupVms { get; }

    /// <summary>"Maintain to keep" requirement groups, same wrapped shape.</summary>
    public IReadOnlyList<QuestRequirementGroupVm> SustainRequirementGroupVms { get; }

    /// <summary>Reward groups reshaped to the dual-shape Link/prose row VM.</summary>
    public IReadOnlyList<QuestRewardGroupVm> RewardGroupVms { get; }

    /// <summary>Objective rows reshaped (nested requirement groups wrapped too).</summary>
    public IReadOnlyList<QuestObjectiveRowVm> ObjectiveRowVms { get; }

    /// <summary>Header Level / Location / Repeatability badge boxes collapsed to
    /// ONE inert Fact strip (pilot StatStrip). The Repeatability box was the
    /// named G-b T11 gold badge — gone by construction.</summary>
    public FactTableVm HeaderStrip { get; }

    /// <summary>
    /// Footer identifier strip (matrix #14 / G-a · ratified E5). The Quest
    /// InternalName is a cross-entity reference key (follow-ups / NPCs / vaults
    /// resolve a quest by it) ⇒ the copyable <c>KEY</c> (Area's path), not an
    /// inert envelope <c>ROW</c>. <see cref="FactFooterVm.None"/> if keyless.
    /// </summary>
    public FactFooterVm Footer { get; }

    // ─── Builders ──────────────────────────────────────────────────────────────────────

    private static EntityChipVm? BuildNpcChip(
        string? internalName,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(internalName)) return null;
        var reference = EntityRef.Npc(internalName!);
        return new EntityChipVm(
            DisplayName: QuestDetailProjector.ResolveNpcDisplayWithArea(refData, resolver, internalName),
            IconId: 0,
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference));
    }

    private static IReadOnlyList<EntityChipVm> BuildItemChips(
        IReadOnlyList<QuestItemRef>? items,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (items is null || items.Count == 0) return [];
        var list = new List<EntityChipVm>(items.Count);
        foreach (var i in items)
        {
            if (string.IsNullOrEmpty(i.Item)) continue;
            var reference = EntityRef.Item(i.Item!);
            var iconId = refData.ItemsByInternalName.TryGetValue(i.Item!, out var item) ? item.IconId : 0;
            var name = resolver.Resolve(reference);
            list.Add(new EntityChipVm(
                DisplayName: i.StackSize > 1 ? $"{name} ×{i.StackSize}" : name,
                IconId: iconId,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference)));
        }
        return list;
    }

    private static IReadOnlyList<EntityChipVm> BuildRecipeRewardChips(
        Quest quest,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (quest.Rewards is null || quest.Rewards.Count == 0) return [];
        var list = new List<EntityChipVm>();
        foreach (var r in quest.Rewards)
        {
            if (r is RecipeReward { Recipe: { } recipeInternalName } && !string.IsNullOrEmpty(recipeInternalName))
            {
                var reference = EntityRef.Recipe(recipeInternalName);
                var iconId = refData.RecipesByInternalName.TryGetValue(recipeInternalName, out var recipe)
                    ? recipe.IconId
                    : 0;
                list.Add(new EntityChipVm(
                    DisplayName: resolver.Resolve(reference),
                    IconId: iconId,
                    Reference: reference,
                    IsNavigable: navigator.CanOpen(reference)));
            }
        }
        return list;
    }

    private static IReadOnlyList<EntityChipVm> BuildRecipeChips(
        IReadOnlyList<string>? recipes,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (recipes is null || recipes.Count == 0) return [];
        var list = new List<EntityChipVm>(recipes.Count);
        foreach (var r in recipes)
        {
            if (string.IsNullOrEmpty(r)) continue;
            var reference = EntityRef.Recipe(r);
            var iconId = refData.RecipesByInternalName.TryGetValue(r, out var recipe) ? recipe.IconId : 0;
            list.Add(new EntityChipVm(
                DisplayName: resolver.Resolve(reference),
                IconId: iconId,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference)));
        }
        return list;
    }

    private static IReadOnlyList<EntityChipVm> BuildQuestChips(
        IReadOnlyList<string>? quests,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (quests is null || quests.Count == 0) return [];
        var list = new List<EntityChipVm>(quests.Count);
        foreach (var q in quests)
        {
            if (string.IsNullOrEmpty(q)) continue;
            var reference = EntityRef.Quest(q);
            list.Add(new EntityChipVm(
                DisplayName: resolver.Resolve(reference),
                IconId: 0,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference)));
        }
        return list;
    }

    private static string? BuildFavorRewardDisplay(Quest quest, IReferenceDataService refData, IEntityNameResolver resolver)
    {
        var favor = quest.Reward_Favor ?? quest.Rewards_Favor ?? 0;
        if (favor <= 0 || string.IsNullOrEmpty(quest.FavorNpc)) return null;
        return $"+{favor:N0} favor with {QuestDetailProjector.ResolveNpcDisplayWithArea(refData, resolver, quest.FavorNpc)}";
    }

    private static string? BuildBadgesDisplay(bool cancellable, bool guild, bool workOrder, string? workOrderSkill)
    {
        var badges = new List<string>(3);
        if (cancellable) badges.Add("Cancellable");
        if (guild) badges.Add("Guild quest");
        if (workOrder)
        {
            badges.Add(string.IsNullOrEmpty(workOrderSkill) ? "Work order" : $"Work order · {workOrderSkill}");
        }
        return badges.Count == 0 ? null : string.Join(" · ", badges);
    }

    private static string ResolveSkillDisplay(IReferenceDataService refData, string skillKey) =>
        refData.Skills.TryGetValue(skillKey, out var s) && !string.IsNullOrEmpty(s.DisplayName) ? s.DisplayName : skillKey;
}

/// <summary>
/// View-side reshape of a <see cref="QuestRequirementDisplay"/> — the dual shape
/// the Recipe pilot's <c>RecipeRequirementRowVm</c> was modelled on, now applied
/// to its origin. Chip row (<c>ChipName</c> + a resolvable <c>Reference</c>) ⇒
/// Structure <see cref="Prefix"/> + a Prose <see cref="Link"/>; prose row ⇒
/// <see cref="Link"/> null (the same object self-switch the legacy template did
/// on <c>ChipName</c>). Wraps the record (its file is out of edit scope).
/// </summary>
public sealed class QuestRequirementRowVm
{
    private QuestRequirementRowVm(string text, string? prefix, LinkVm? link)
    {
        Text = text;
        Prefix = prefix;
        Link = link;
    }

    /// <summary>Accessible / fallback prose (used when <see cref="Link"/> is null).</summary>
    public string Text { get; }

    /// <summary>Inline field-label prefix shown before the <see cref="Link"/> (Structure tier).</summary>
    public string? Prefix { get; }

    /// <summary>The inline navigable cross-link, or null for a prose-only row.</summary>
    public LinkVm? Link { get; }

    public static QuestRequirementRowVm From(QuestRequirementDisplay d) =>
        new(d.Text, d.Prefix,
            !string.IsNullOrEmpty(d.ChipName) && d.Reference is { } r
                ? new LinkVm(d.ChipName!, LinkVm.GlyphFor(r.Kind), d.Reference, d.IsNavigable)
                : null);
}

/// <summary>A requirement group whose rows are the wrapped dual-shape VM.</summary>
public sealed class QuestRequirementGroupVm
{
    private QuestRequirementGroupVm(string label, IReadOnlyList<QuestRequirementRowVm> requirements)
    {
        Label = label;
        Requirements = requirements;
    }

    public string Label { get; }
    public IReadOnlyList<QuestRequirementRowVm> Requirements { get; }

    public static QuestRequirementGroupVm From(QuestRequirementGroup g) =>
        new(g.Label, g.Requirements.Select(QuestRequirementRowVm.From).ToList());
}

/// <summary>View-side reshape of a <see cref="QuestRewardDisplay"/> — identical
/// dual shape to <see cref="QuestRequirementRowVm"/>.</summary>
public sealed class QuestRewardRowVm
{
    private QuestRewardRowVm(string text, string? prefix, LinkVm? link)
    {
        Text = text;
        Prefix = prefix;
        Link = link;
    }

    public string Text { get; }
    public string? Prefix { get; }
    public LinkVm? Link { get; }

    public static QuestRewardRowVm From(QuestRewardDisplay d) =>
        new(d.Text, d.Prefix,
            !string.IsNullOrEmpty(d.ChipName) && d.Reference is { } r
                ? new LinkVm(d.ChipName!, LinkVm.GlyphFor(r.Kind), d.Reference, d.IsNavigable)
                : null);
}

/// <summary>A reward group whose rows are the wrapped dual-shape VM.</summary>
public sealed class QuestRewardGroupVm
{
    private QuestRewardGroupVm(string label, IReadOnlyList<QuestRewardRowVm> rewards)
    {
        Label = label;
        Rewards = rewards;
    }

    public string Label { get; }
    public IReadOnlyList<QuestRewardRowVm> Rewards { get; }

    public static QuestRewardGroupVm From(QuestRewardGroup g) =>
        new(g.Label, g.Rewards.Select(QuestRewardRowVm.From).ToList());
}

/// <summary>View-side reshape of a <see cref="QuestObjectiveRow"/> — its nested
/// requirement groups are recursively wrapped to the dual-shape VM.</summary>
public sealed class QuestObjectiveRowVm
{
    private QuestObjectiveRowVm(
        int index, string description, int? number,
        IReadOnlyList<QuestRequirementGroupVm> nestedRequirements)
    {
        Index = index;
        Description = description;
        Number = number;
        NestedRequirements = nestedRequirements;
    }

    public int Index { get; }
    public string Description { get; }
    public int? Number { get; }
    public IReadOnlyList<QuestRequirementGroupVm> NestedRequirements { get; }

    public static QuestObjectiveRowVm From(QuestObjectiveRow o) =>
        new(o.Index, o.Description, o.Number,
            o.NestedRequirements.Select(QuestRequirementGroupVm.From).ToList());
}
