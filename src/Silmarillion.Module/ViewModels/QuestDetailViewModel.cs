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
        FavorRewardDisplay = BuildFavorRewardDisplay(quest, refData, nameResolver);
        BadgesDisplay = BuildBadgesDisplay(IsCancellable, IsGuildQuest, IsWorkOrder, WorkOrderSkillDisplay);

        OpenEntityCommand = openEntityCommand;
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
