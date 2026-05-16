using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Crafting;

/// <summary>
/// Reverse index "item <c>InternalName</c> → every recipe that produces it",
/// built by scanning each recipe's <c>ResultItems</c> (and <c>ProtoResultItems</c>
/// as a fallback). Unlike the old Celebrimbor-internal "first recipe wins" lookup,
/// this keeps the *full set of alternatives* (in reference-data enumeration order,
/// stable across runs) and lets the caller decide which to pick — Celebrimbor's
/// shopping list, the #121 multi-recipe picker, and the #227 planner all need the
/// same producer graph but choose differently. (#226, supersedes #121.)
/// </summary>
public sealed class RecipeProducerIndex
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Recipe>> _byItem;

    private RecipeProducerIndex(IReadOnlyDictionary<string, IReadOnlyList<Recipe>> byItem)
        => _byItem = byItem;

    public static RecipeProducerIndex Build(IReferenceDataService refData)
    {
        var map = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);

        void Register(Recipe recipe, IReadOnlyList<RecipeResultItem>? results)
        {
            if (results is null) return;
            foreach (var result in results)
            {
                if (!refData.Items.TryGetValue(result.ItemCode, out var item)) continue;
                if (string.IsNullOrEmpty(item.InternalName)) continue;
                if (!map.TryGetValue(item.InternalName!, out var list))
                    map[item.InternalName!] = list = [];
                // De-dup: a recipe that lists the same item in both ResultItems and
                // ProtoResultItems should appear once as a producer of it.
                if (!list.Contains(recipe)) list.Add(recipe);
            }
        }

        foreach (var recipe in refData.Recipes.Values)
        {
            Register(recipe, recipe.ResultItems);
            Register(recipe, recipe.ProtoResultItems);
        }

        return new RecipeProducerIndex(
            map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Recipe>)kv.Value, StringComparer.Ordinal));
    }

    /// <summary>Every item that at least one recipe produces → its producing recipes.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Recipe>> ByItemInternalName => _byItem;

    public bool HasProducer(string itemInternalName) => _byItem.ContainsKey(itemInternalName);

    /// <summary>Producing recipes for <paramref name="itemInternalName"/> (empty if none).</summary>
    public IReadOnlyList<Recipe> Alternatives(string itemInternalName)
        => _byItem.TryGetValue(itemInternalName, out var list) ? list : [];

    /// <summary>
    /// First producing recipe in enumeration order — the deterministic default when a
    /// caller doesn't supply its own <see cref="RecipeChoicePolicy"/>. Reproduces the
    /// old "first recipe wins" behaviour exactly.
    /// </summary>
    public bool TryGetDefault(string itemInternalName, out Recipe recipe)
    {
        if (_byItem.TryGetValue(itemInternalName, out var list) && list.Count > 0)
        {
            recipe = list[0];
            return true;
        }
        recipe = null!;
        return false;
    }
}

/// <summary>
/// Picks which producing recipe to use for an item when several can make it. The
/// expander stays free of optimisation concerns — Celebrimbor's UI / the #227
/// planner own the choice. Must return one of <paramref name="alternatives"/>.
/// </summary>
public delegate Recipe RecipeChoicePolicy(string itemInternalName, IReadOnlyList<Recipe> alternatives);
