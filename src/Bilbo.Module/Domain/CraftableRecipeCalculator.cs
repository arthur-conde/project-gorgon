using System.Globalization;
using Gorgon.Shared.Character;
using Gorgon.Shared.Reference;

namespace Bilbo.Domain;

/// <summary>
/// Cross-references available storage rows against the recipe catalog to
/// produce one row per recipe with craftability, known-status, and skill-gate
/// decoration drawn from the active <see cref="CharacterSnapshot"/>.
/// </summary>
public static class CraftableRecipeCalculator
{
    public static IReadOnlyList<CraftableRecipeRow> Compute(
        IReadOnlyList<StorageItemRow> availableRows,
        IReferenceDataService refData,
        CharacterSnapshot? character,
        ConfidenceLevel confidence)
    {
        var have = new Dictionary<long, long>();
        foreach (var row in availableRows)
        {
            var key = (long)row.TypeID;
            have.TryGetValue(key, out var sum);
            have[key] = sum + row.StackSize;
        }

        var result = new List<CraftableRecipeRow>(refData.Recipes.Count);
        foreach (var recipe in refData.Recipes.Values)
        {
            var ingredientParts = new List<string>(recipe.Ingredients.Count);
            var missingParts = new List<string>();
            var maxCraftable = int.MaxValue;

            foreach (var ingredient in recipe.Ingredients)
            {
                var itemName = refData.Items.TryGetValue(ingredient.ItemCode, out var entry)
                    ? entry.Name
                    : $"#{ingredient.ItemCode}";
                var part = $"{itemName} x{ingredient.StackSize}";
                if (ingredient.ChanceToConsume is { } chance && chance < 1f)
                    part += $" (p={Math.Round(chance * 100).ToString("0", CultureInfo.InvariantCulture)}%)";
                ingredientParts.Add(part);

                have.TryGetValue(ingredient.ItemCode, out var available);
                var perIngredient = ConsumeQuantile.MaxCrafts(available, ingredient.StackSize, ingredient.ChanceToConsume, confidence);
                if (perIngredient < maxCraftable)
                    maxCraftable = perIngredient;

                if (available < ingredient.StackSize)
                    missingParts.Add($"{itemName} x{ingredient.StackSize} (have {available})");
            }

            if (recipe.Ingredients.Count == 0)
                maxCraftable = 0;

            var (resultName, resultStack) = ResolveResult(recipe, refData);
            var (charLevel, skillMet) = ResolveSkill(recipe, character);
            var (isKnown, timesCompleted) = ResolveKnown(recipe, character);

            result.Add(new CraftableRecipeRow(
                Name: recipe.Name,
                Skill: recipe.Skill,
                SkillLevelReq: recipe.SkillLevelReq,
                CharacterSkillLevel: charLevel,
                SkillLevelMet: skillMet,
                IsKnown: isKnown,
                TimesCompleted: timesCompleted,
                MaxCraftable: maxCraftable,
                Ingredients: string.Join(", ", ingredientParts),
                MissingIngredients: maxCraftable > 0 ? "" : string.Join(", ", missingParts),
                ResultItem: resultName,
                ResultStackSize: resultStack,
                IconId: recipe.IconId,
                InternalName: recipe.InternalName));
        }

        return result;
    }

    private static (string name, int stack) ResolveResult(RecipeEntry recipe, IReferenceDataService refData)
    {
        if (recipe.ResultItems.Count == 0)
            return ("(no result)", 0);
        var first = recipe.ResultItems[0];
        var name = refData.Items.TryGetValue(first.ItemCode, out var entry) ? entry.Name : $"#{first.ItemCode}";
        return (name, first.StackSize);
    }

    private static (int? level, bool met) ResolveSkill(RecipeEntry recipe, CharacterSnapshot? character)
    {
        if (character is null || string.IsNullOrEmpty(recipe.Skill))
            return (null, false);
        if (!character.Skills.TryGetValue(recipe.Skill, out var skill))
            return (null, false);
        var effective = skill.Level + skill.BonusLevels;
        return (effective, effective >= recipe.SkillLevelReq);
    }

    private static (bool known, int times) ResolveKnown(RecipeEntry recipe, CharacterSnapshot? character)
    {
        if (character is null)
            return (false, 0);
        if (character.RecipeCompletions.TryGetValue(recipe.InternalName, out var times))
            return (true, times);
        return (false, 0);
    }
}
