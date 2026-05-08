using Elrond.Domain;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;

namespace Elrond.Services;

/// <summary>
/// Pure computation engine that cross-references CDN reference data with a
/// character snapshot to produce skill-leveling analysis.
/// </summary>
public sealed class SkillAdvisorEngine
{
    private readonly IReferenceDataService _ref;

    public SkillAdvisorEngine(IReferenceDataService referenceData)
    {
        _ref = referenceData;
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
    internal static string FilingSkillOf(RecipeEntry recipe) =>
        string.IsNullOrEmpty(recipe.SortSkill) ? recipe.RewardSkill : recipe.SortSkill;

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
            character.Skills.TryGetValue(recipe.RewardSkill, out var rewardCharSkill);
            var rewardLevel = rewardCharSkill?.Level ?? 0;

            // Gating skill = recipe.Skill (paired with SkillLevelReq). For most recipes this
            // matches the section, but in umbrella sections (Phrenology files Phrenology_Goblins,
            // Cooking files Fishing-rewarding fish stew) the gate sits on a different skill.
            // Read the player's level there so the "Craftable only" filter compares apples to apples.
            var gatingSkillLevel = !string.IsNullOrEmpty(recipe.Skill)
                && character.Skills.TryGetValue(recipe.Skill, out var gatingCharSkill)
                    ? gatingCharSkill.Level
                    : 0;

            var isKnown = character.RecipeCompletions.TryGetValue(recipe.InternalName, out var timesCompleted);
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
            else if (recipe.RewardSkill.Equals(sectionKey, StringComparison.Ordinal))
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
                .Select(i => i switch
                {
                    RecipeItemIngredient byItem => _ref.Items.TryGetValue(byItem.ItemCode, out var item)
                        ? new RecipeIngredientDisplay(item.Name, item.IconId, byItem.StackSize, byItem.ChanceToConsume)
                        : new RecipeIngredientDisplay($"Item #{byItem.ItemCode}", 0, byItem.StackSize, byItem.ChanceToConsume),
                    RecipeKeywordIngredient kw => new RecipeIngredientDisplay(
                        kw.Desc ?? $"Any {ItemKeywordIndex.Humanise(kw.ItemKeys)}",
                        IconId: 0, kw.StackSize, kw.ChanceToConsume),
                    _ => new RecipeIngredientDisplay("(unknown ingredient)", 0, i.StackSize, i.ChanceToConsume),
                })
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

            var rewardSkillDisplayName = _ref.Skills.TryGetValue(recipe.RewardSkill, out var rewardSkillEntry)
                ? rewardSkillEntry.DisplayName
                : recipe.RewardSkill;
            var rewardDiffersFromSection = !recipe.RewardSkill.Equals(sectionKey, StringComparison.Ordinal);

            recipeAnalyses.Add(new RecipeAnalysis(
                recipe.Key,
                recipe.Name,
                recipe.InternalName,
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
                RewardSkill: recipe.RewardSkill,
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
        // so the section header should degrade to "—" placeholders. Detection by
        // missing/None XpTable is robust without needing IsUmbrellaSkill projected.
        var sectionEntry = _ref.Skills.TryGetValue(sectionKey, out var se) ? se : null;
        var isUmbrellaSection = sectionEntry is null
            || string.IsNullOrEmpty(sectionEntry.XpTable)
            || sectionEntry.XpTable.Equals("None", StringComparison.Ordinal);

        return new SkillAnalysis(
            sectionKey,
            sectionLevel,
            sectionCurrentXp,
            sectionXpNeeded,
            sectionXpRemaining,
            recipeAnalyses,
            milestones,
            goalLevel is { } g && g > sectionLevel ? goalLevel : null,
            IsUmbrellaSection: isUmbrellaSection);
    }

    internal int ComputeEffectiveXp(RecipeEntry recipe, int playerLevel)
    {
        if (recipe.RewardSkillXpDropOffLevel is not { } dropOffLevel) return recipe.RewardSkillXp;
        if (recipe.RewardSkillXpDropOffPct is not { } dropOffPct) return recipe.RewardSkillXp;

        if (playerLevel < dropOffLevel) return recipe.RewardSkillXp;

        var rate = recipe.RewardSkillXpDropOffRate ?? 1;
        if (rate <= 0) rate = 1;

        // Each 'rate' levels past drop-off reduces by dropOffPct
        var levelsPast = playerLevel - dropOffLevel;
        var reductions = levelsPast / rate;
        var remaining = 1.0f;
        for (var i = 0; i < reductions; i++)
        {
            remaining *= (1.0f - dropOffPct);
            if (remaining <= 0) return 0;
        }

        return Math.Max(0, (int)(recipe.RewardSkillXp * remaining));
    }

    /// <summary>
    /// Computes the total XP needed from the current position to reach <paramref name="goalLevel"/>.
    /// </summary>
    internal long ComputeXpToGoal(string skillName, int currentLevel, long currentXp, long currentLevelXpNeeded, int goalLevel)
    {
        // XP remaining in the current level
        var total = currentLevelXpNeeded - currentXp;
        if (total < 0) total = 0;

        var xpAmounts = ResolveXpTable(skillName);
        if (xpAmounts is null) return total;

        // Add XP for each full level between currentLevel+1 and goalLevel-1,
        // plus XP for goalLevel-1 index (to reach goalLevel)
        for (var lvl = currentLevel + 1; lvl < goalLevel; lvl++)
        {
            if (lvl - 1 < xpAmounts.Count)
                total += xpAmounts[lvl - 1];
            else
                break;
        }

        return total;
    }

    /// <summary>Resolves the XP amounts array for a given skill.</summary>
    internal IReadOnlyList<long>? ResolveXpTable(string skillName)
    {
        if (_ref.Skills.TryGetValue(skillName, out var skillEntry) &&
            !string.IsNullOrEmpty(skillEntry.XpTable) &&
            _ref.XpTables.TryGetValue(skillEntry.XpTable, out var xpTable))
        {
            return xpTable.XpAmounts;
        }
        return null;
    }

    private IReadOnlyList<XpMilestone> BuildMilestones(
        string skillName, int currentLevel, long currentXp, long currentLevelXpNeeded, int maxLevels)
    {
        // Resolve XP table for the skill
        IReadOnlyList<long>? xpAmounts = null;
        if (_ref.Skills.TryGetValue(skillName, out var skillEntry) &&
            !string.IsNullOrEmpty(skillEntry.XpTable) &&
            _ref.XpTables.TryGetValue(skillEntry.XpTable, out var xpTable))
        {
            xpAmounts = xpTable.XpAmounts;
        }

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
