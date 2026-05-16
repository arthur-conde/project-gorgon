using Elrond.Domain;
using Mithril.Leveling;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;

namespace Elrond.Services;

/// <summary>
/// Pure computation engine that cross-references CDN reference data with a
/// character snapshot to produce skill-leveling analysis. The skill-XP math
/// (drop-off, first-time bonus, XP-to-goal, table resolution) lives in shared
/// <see cref="LevelingMath"/> per #225; this engine owns the Elrond-specific
/// projection to <see cref="SkillAnalysis"/> / <see cref="RecipeAnalysis"/> rows
/// and the cookbook-section model.
/// </summary>
public sealed class SkillAdvisorEngine
{
    private readonly IReferenceDataService _ref;
    private readonly LevelingMath _math;

    public SkillAdvisorEngine(IReferenceDataService referenceData, LevelingMath? math = null)
    {
        _ref = referenceData;
        _math = math ?? new LevelingMath(referenceData);
    }

    /// <summary>
    /// Returns all cookbook section names — distinct values of
    /// <c>SortSkill ?? RewardSkill</c> across recipes that award XP, sorted
    /// alphabetically. A "section" is the in-game cookbook category a recipe
    /// is filed under: typically the same as <c>RewardSkill</c>, but for some
    /// recipes (fish-based foods → <c>Cooking</c>) the filing differs from the
    /// XP-earning skill.
    /// </summary>
    public IReadOnlyList<string> GetCookbookSections()
    {
        var sections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipe in _ref.Recipes.Values)
        {
            if (recipe.RewardSkillXp <= 0 && recipe.RewardSkillXpFirstTime <= 0) continue;
            var section = FilingSkillOf(recipe);
            if (!string.IsNullOrEmpty(section))
                sections.Add(section);
        }
        var list = sections.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>The cookbook section a recipe is filed under: <c>SortSkill</c> if set, else <c>RewardSkill</c>.</summary>
    internal static string FilingSkillOf(Recipe recipe) =>
        string.IsNullOrEmpty(recipe.SortSkill) ? recipe.RewardSkill ?? "" : recipe.SortSkill!;

    /// <summary>
    /// Project a polymorphic <see cref="RecipeIngredient"/> to a display-ready
    /// <see cref="RecipeIngredientDisplay"/>. Item-coded slots resolve via the
    /// items catalog; keyword slots show <c>Desc</c> or a humanised keyword list.
    /// </summary>
    private static RecipeIngredientDisplay ProjectIngredientDisplay(RecipeIngredient i, IReferenceDataService refData)
    {
        switch (i)
        {
            case RecipeItemIngredient itemIng:
                return refData.Items.TryGetValue(itemIng.ItemCode, out var item)
                    ? new RecipeIngredientDisplay(item.Name ?? $"Item #{itemIng.ItemCode}", item.IconId, i.StackSize, (float?)i.ChanceToConsume)
                    : new RecipeIngredientDisplay($"Item #{itemIng.ItemCode}", 0, i.StackSize, (float?)i.ChanceToConsume);
            case RecipeKeywordIngredient kwIng when kwIng.ItemKeys.Count > 0:
                return new RecipeIngredientDisplay(
                    kwIng.Desc ?? $"Any {ItemKeywordIndex.Humanise(kwIng.ItemKeys)}",
                    IconId: 0, i.StackSize, (float?)i.ChanceToConsume);
            default:
                return new RecipeIngredientDisplay("(unknown ingredient)", 0, i.StackSize, (float?)i.ChanceToConsume);
        }
    }

    /// <summary>
    /// Analyze a cookbook section for a given character: every recipe filed under
    /// <paramref name="sectionKey"/>, with each recipe's metrics (effective XP,
    /// completions-to-level, first-time bonus) computed against its own
    /// <c>RewardSkill</c> — which may differ from the section. The section's
    /// header level/XP read from the character's progress in the section's skill
    /// (when present); recipes whose RewardSkill differs from the section show
    /// completions toward their own next level (no goal-aware projection).
    /// </summary>
    public SkillAnalysis? Analyze(string sectionKey, CharacterSnapshot character, bool includeZeroXp = false, int? goalLevel = null)
    {
        // The section must correspond to a known character skill — exotic filings
        // (e.g. Race_Fae) that aren't real character skills can't be advised on
        // because the header progress bar has no data to show. The picker filters
        // sections by character.Skills.ContainsKey upstream, so this is a guard
        // for direct callers (deep links to unfamiliar sections, tests).
        if (!character.Skills.TryGetValue(sectionKey, out var sectionCharSkill))
            return null;

        var sectionLevel = sectionCharSkill.Level;
        var sectionBonusLevels = sectionCharSkill.BonusLevels;
        var sectionCurrentXp = sectionCharSkill.XpTowardNextLevel;
        var sectionXpNeeded = sectionCharSkill.XpNeededForNextLevel;

        long sectionXpRemaining;
        if (goalLevel is { } goal && goal > sectionLevel)
            sectionXpRemaining = ComputeXpToGoal(sectionKey, sectionLevel, sectionCurrentXp, sectionXpNeeded, goal);
        else
            sectionXpRemaining = sectionXpNeeded - sectionCurrentXp;
        if (sectionXpRemaining < 0) sectionXpRemaining = 0;

        var recipeAnalyses = new List<RecipeAnalysis>();
        foreach (var recipe in _ref.Recipes.Values)
        {
            if (!FilingSkillOf(recipe).Equals(sectionKey, StringComparison.Ordinal)) continue;
            if (recipe.RewardSkillXp <= 0 && recipe.RewardSkillXpFirstTime <= 0) continue;
            if (!includeZeroXp && recipe.RewardSkillXp <= 0) continue;

            // Per-recipe character context: we need the character's level in this recipe's
            // RewardSkill to compute drop-off and completions-to-its-own-level. May be null
            // if the character hasn't learned the reward skill yet.
            var rewardSkill = recipe.RewardSkill ?? "";
            character.Skills.TryGetValue(rewardSkill, out var rewardCharSkill);
            var rewardLevel = rewardCharSkill?.Level ?? 0;

            // Gating skill = recipe.Skill (paired with SkillLevelReq). For most recipes this
            // matches the section, but in umbrella sections (Phrenology files Phrenology_Goblins,
            // Cooking files Fishing-rewarding fish stew) the gate sits on a different skill.
            // Read the player's level there so the "Craftable only" filter compares apples to apples.
            var gatingSkillLevel = !string.IsNullOrEmpty(recipe.Skill)
                && character.Skills.TryGetValue(recipe.Skill!, out var gatingCharSkill)
                    ? gatingCharSkill.Level
                    : 0;

            var internalName = recipe.InternalName ?? "";
            var timesCompleted = 0;
            var isKnown = !string.IsNullOrEmpty(internalName)
                && character.RecipeCompletions.TryGetValue(internalName, out timesCompleted);
            var firstTimeBonusAvailable = isKnown && timesCompleted == 0 && recipe.RewardSkillXpFirstTime > 0;

            var effectiveXp = ComputeEffectiveXp(recipe, rewardLevel);

            // Goal-awareness: if the recipe rewards the section's skill, share the
            // section-level goal. Otherwise (mixed-reward recipes filed here) just
            // show completions to that recipe's own next level.
            long xpRemainingForThisRecipe;
            if (rewardCharSkill is null)
            {
                xpRemainingForThisRecipe = 0;
            }
            else if (rewardSkill.Equals(sectionKey, StringComparison.Ordinal))
            {
                xpRemainingForThisRecipe = sectionXpRemaining;
            }
            else
            {
                xpRemainingForThisRecipe = rewardCharSkill.XpNeededForNextLevel - rewardCharSkill.XpTowardNextLevel;
                if (xpRemainingForThisRecipe < 0) xpRemainingForThisRecipe = 0;
            }

            int? completionsToLevel = null;
            if (effectiveXp > 0 && xpRemainingForThisRecipe > 0)
            {
                if (firstTimeBonusAvailable && recipe.RewardSkillXpFirstTime > 0)
                {
                    var afterFirst = xpRemainingForThisRecipe - recipe.RewardSkillXpFirstTime;
                    if (afterFirst <= 0)
                        completionsToLevel = 1;
                    else
                        completionsToLevel = 1 + (int)Math.Ceiling((double)afterFirst / effectiveXp);
                }
                else
                {
                    completionsToLevel = (int)Math.Ceiling((double)xpRemainingForThisRecipe / effectiveXp);
                }
            }

            var ingredients = recipe.Ingredients
                .Select(i => ProjectIngredientDisplay(i, _ref))
                .ToList();

            var craftedOutputs = ResultEffectsParser.ParseCraftedGear(recipe.ResultEffects, _ref);

            var complexity = recipe.Ingredients
                .Sum(i => i.StackSize * (double)(i.ChanceToConsume ?? 1.0f));
            var nextCraftXp = firstTimeBonusAvailable && recipe.RewardSkillXpFirstTime > 0
                ? recipe.RewardSkillXpFirstTime
                : effectiveXp;
            double? efficiency = nextCraftXp > 0 && complexity > 0
                ? nextCraftXp / complexity
                : null;

            var rewardSkillDisplayName = _ref.Skills.TryGetValue(rewardSkill, out var rewardSkillEntry)
                ? rewardSkillEntry.DisplayName
                : rewardSkill;
            var rewardDiffersFromSection = !rewardSkill.Equals(sectionKey, StringComparison.Ordinal);

            recipeAnalyses.Add(new RecipeAnalysis(
                recipe.Key,
                recipe.Name ?? "",
                internalName,
                recipe.IconId,
                recipe.SkillLevelReq,
                recipe.RewardSkillXp,
                recipe.RewardSkillXpFirstTime,
                timesCompleted,
                isKnown,
                firstTimeBonusAvailable,
                effectiveXp,
                nextCraftXp,
                completionsToLevel,
                complexity,
                efficiency,
                ingredients,
                craftedOutputs,
                RewardSkill: rewardSkill,
                RewardSkillDisplayName: rewardSkillDisplayName,
                RewardSkillCurrentLevel: rewardCharSkill?.Level ?? 0,
                RewardSkillCurrentXp: rewardCharSkill?.XpTowardNextLevel ?? 0,
                RewardSkillXpNeededForNextLevel: rewardCharSkill?.XpNeededForNextLevel ?? 0,
                RewardSkillDiffersFromSection: rewardDiffersFromSection,
                GatingSkill: recipe.Skill ?? string.Empty,
                GatingSkillCurrentLevel: gatingSkillLevel));
        }

        recipeAnalyses.Sort((a, b) =>
        {
            var cmp = a.LevelRequired.CompareTo(b.LevelRequired);
            if (cmp != 0) return cmp;
            return b.EffectiveXp.CompareTo(a.EffectiveXp);
        });

        var milestones = BuildMilestones(sectionKey, sectionLevel, sectionCurrentXp, sectionXpNeeded, maxLevels: 10);

        // Umbrella skills (Phrenology, Anatomy, Augmentation, …) have no XpTable,
        // so the section header degrades the XP fraction / progress bar / remaining
        // line to "—". Level + BonusLevels still come from the export and render
        // normally. Detection by missing/None XpTable is robust without needing
        // IsUmbrellaSkill projected.
        var sectionEntry = _ref.Skills.TryGetValue(sectionKey, out var se) ? se : null;
        var isUmbrellaSection = sectionEntry is null
            || string.IsNullOrEmpty(sectionEntry.XpTable)
            || sectionEntry.XpTable.Equals("None", StringComparison.Ordinal);

        return new SkillAnalysis(
            sectionKey,
            sectionLevel,
            sectionBonusLevels,
            sectionCurrentXp,
            sectionXpNeeded,
            sectionXpRemaining,
            recipeAnalyses,
            milestones,
            goalLevel is { } g && g > sectionLevel ? goalLevel : null,
            IsUmbrellaSection: isUmbrellaSection);
    }

    // Math delegated to shared Mithril.Leveling (#225). These thin pass-throughs
    // keep the engine's and LevelingSimulator's existing call sites untouched.
    internal int ComputeEffectiveXp(Recipe recipe, int playerLevel)
        => _math.EffectiveXpPerCraft(recipe, playerLevel);

    /// <summary>
    /// Computes the total XP needed from the current position to reach <paramref name="goalLevel"/>.
    /// </summary>
    internal long ComputeXpToGoal(string skillName, int currentLevel, long currentXp, long currentLevelXpNeeded, int goalLevel)
        => _math.XpToGoal(skillName, currentLevel, currentXp, currentLevelXpNeeded, goalLevel);

    /// <summary>Resolves the XP amounts array for a given skill.</summary>
    internal IReadOnlyList<long>? ResolveXpTable(string skillName)
        => _math.ResolveXpTable(skillName);

    private IReadOnlyList<XpMilestone> BuildMilestones(
        string skillName, int currentLevel, long currentXp, long currentLevelXpNeeded, int maxLevels)
    {
        var xpAmounts = _math.ResolveXpTable(skillName);

        var milestones = new List<XpMilestone>();
        var cumulative = currentLevelXpNeeded - currentXp; // XP remaining in current level

        for (var lvl = currentLevel + 1; lvl <= currentLevel + maxLevels; lvl++)
        {
            // XpAmounts is 0-indexed: index 0 = XP for level 1, index N-1 = XP for level N
            long xpForLevel;
            if (xpAmounts is not null && lvl - 1 < xpAmounts.Count)
                xpForLevel = xpAmounts[lvl - 1];
            else
                break; // no data for this level

            milestones.Add(new XpMilestone(lvl, xpForLevel, cumulative));
            cumulative += xpForLevel;
        }

        return milestones;
    }
}
