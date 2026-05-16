using Celebrimbor.Domain;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Crafting;
using Mithril.Shared.Reference;
using Mithril.Shared.Storage;

namespace Celebrimbor.Services;

/// <summary>
/// Celebrimbor's shopping-list aggregation. The demand-driven expansion engine
/// (producer graph, multi-output crediting, on-hand-aware shortfall recursion)
/// lives in shared <see cref="RecipeExpander"/> (#226); this class owns only the
/// Celebrimbor-specific projection: the stable chain skeleton, keyword-slot rows,
/// crafting-step depth, on-hand/override/location plumbing, and the sort.
/// </summary>
public sealed class RecipeAggregator
{
    /// <summary>
    /// Accumulate ingredient demand across every entry in <paramref name="entries"/>.
    /// When <paramref name="expansionDepth"/> is greater than zero, any aggregated
    /// ingredient that also resolves as a recipe is replaced by its own ingredients,
    /// up to the depth limit and with cycle protection.
    /// </summary>
    public IReadOnlyList<AggregatedIngredient> Aggregate(
        IEnumerable<CraftListEntry> entries,
        int expansionDepth,
        IReferenceDataService refData,
        IReadOnlyDictionary<string, int>? onHandByInternalName = null,
        IReadOnlyDictionary<string, IReadOnlyList<IngredientLocation>>? locationsByInternalName = null,
        IReadOnlyDictionary<string, int>? overridesByInternalName = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? ownedInternalNamesByKeyword = null)
    {
        var expander = new RecipeExpander(refData);

        var demand = new Dictionary<string, double>(StringComparer.Ordinal);
        var seeds = new List<string>();
        var keywordSlots = new Dictionary<string, KeywordSlot>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry.Quantity <= 0) continue;
            if (!refData.RecipesByInternalName.TryGetValue(entry.RecipeInternalName, out var recipe)) continue;

            // Seed demand with the target recipe's *output* item, so it flows through
            // expansion/projection like any other row. Raw ingredients only appear when
            // expansionDepth > 0 pulls them in.
            var output = FindPrimaryOutput(recipe, refData);
            if (output is null || string.IsNullOrEmpty(output.InternalName)) continue;
            var outInternal = output.InternalName!;
            demand[outInternal] = demand.TryGetValue(outInternal, out var existing)
                ? existing + entry.Quantity
                : entry.Quantity;
            seeds.Add(outInternal);
        }

        // First producer in enumeration order — reproduces the old "first recipe wins"
        // lookup for the chain-skeleton / depth passes; the shared expander uses the
        // same default policy internally so the two stay consistent.
        var producers = expander.Producers.ByItemInternalName
            .ToDictionary(kv => kv.Key, kv => kv.Value[0], StringComparer.Ordinal);

        // Chain pass: everything the plan touches at this depth, shortfall-agnostic.
        // Keeps the shopping list visible as a stable skeleton when the demand pass
        // collapses rows to zero (e.g. user overrides a target to match its needed count).
        var chainItems = BuildChainItems(seeds, expansionDepth, refData, producers, keywordSlots);

        if (expansionDepth > 0)
            expander.Expand(demand, expansionDepth, onHandByInternalName, overridesByInternalName, keywordSlots);

        // Backfill chain items the demand pass dropped or never reached, so projection
        // emits them as 0/0 rows (IsCraftReady=true) rather than hiding the step.
        foreach (var chainItem in chainItems)
        {
            if (!demand.ContainsKey(chainItem)) demand[chainItem] = 0;
        }

        var depthCache = new Dictionary<string, int>(StringComparer.Ordinal);

        var rows = new List<AggregatedIngredient>(demand.Count);
        foreach (var (itemName, expected) in demand)
        {
            if (RecipeExpander.IsKeywordKey(itemName))
            {
                if (!keywordSlots.TryGetValue(itemName, out var spec)) continue;
                rows.Add(BuildKeywordRow(itemName, spec, expected, refData,
                    onHandByInternalName, locationsByInternalName, overridesByInternalName,
                    ownedInternalNamesByKeyword));
                continue;
            }

            if (!refData.ItemsByInternalName.TryGetValue(itemName, out var item)) continue;

            var totalNeeded = (int)Math.Ceiling(expected);
            var detected = onHandByInternalName is not null && onHandByInternalName.TryGetValue(itemName, out var on) ? on : 0;
            int? overrideCount = overridesByInternalName is not null && overridesByInternalName.TryGetValue(itemName, out var ov) ? ov : null;
            var locations = locationsByInternalName is not null && locationsByInternalName.TryGetValue(itemName, out var locs) ? locs : [];
            var primaryTag = item.Keywords?.FirstOrDefault()?.Tag ?? "Misc";
            var isAlsoRecipe = producers.ContainsKey(itemName);
            var depth = ComputeDepth(itemName, producers, refData, depthCache, new HashSet<string>(StringComparer.Ordinal));

            rows.Add(new AggregatedIngredient(
                ItemInternalName: itemName,
                ItemId: item.Id,
                DisplayName: item.Name ?? itemName,
                IconId: item.IconId,
                PrimaryTag: primaryTag,
                TotalNeeded: totalNeeded,
                ExpectedNeeded: expected,
                OnHandDetected: detected,
                OnHandOverride: overrideCount,
                Locations: locations,
                IsAlsoRecipe: isAlsoRecipe,
                Depth: depth));
        }

        return rows
            .OrderBy(r => r.Depth)
            .ThenBy(r => r.PrimaryTag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AggregatedIngredient BuildKeywordRow(
        string syntheticKey,
        KeywordSlot spec,
        double expected,
        IReferenceDataService refData,
        IReadOnlyDictionary<string, int>? onHandByInternalName,
        IReadOnlyDictionary<string, IReadOnlyList<IngredientLocation>>? locationsByInternalName,
        IReadOnlyDictionary<string, int>? overridesByInternalName,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? ownedInternalNamesByKeyword)
    {
        var keys = spec.Keys;
        var humanised = ItemKeywordIndex.Humanise(keys);
        var displayName = string.IsNullOrEmpty(spec.Desc) ? humanised : spec.Desc;
        var primaryTag = keys.Count > 0 ? keys[0] : "Misc";

        var matchingOwned = ResolveOwnedMatching(keys, ownedInternalNamesByKeyword);

        var detected = 0;
        var locations = new List<IngredientLocation>();
        foreach (var name in matchingOwned)
        {
            if (onHandByInternalName is not null && onHandByInternalName.TryGetValue(name, out var c))
                detected += c;
            if (locationsByInternalName is not null && locationsByInternalName.TryGetValue(name, out var locs))
                locations.AddRange(locs);
        }

        int? overrideCount = overridesByInternalName is not null
            && overridesByInternalName.TryGetValue(syntheticKey, out var ov) ? ov : null;

        return new AggregatedIngredient(
            ItemInternalName: syntheticKey,
            ItemId: 0,
            DisplayName: displayName,
            IconId: 0,
            PrimaryTag: primaryTag,
            TotalNeeded: (int)Math.Ceiling(expected),
            ExpectedNeeded: expected,
            OnHandDetected: detected,
            OnHandOverride: overrideCount,
            Locations: locations,
            IsAlsoRecipe: false,
            Depth: 0,
            KeywordsLabel: $"any {humanised}");
    }

    /// <summary>
    /// Resolve the set of owned InternalNames that satisfy every keyword in
    /// <paramref name="keys"/> (AND-match). Single-key lookups hit the inverted
    /// index directly; multi-key intersects per-tag sets.
    /// </summary>
    private static IReadOnlyCollection<string> ResolveOwnedMatching(
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? ownedInternalNamesByKeyword)
    {
        if (keys.Count == 0 || ownedInternalNamesByKeyword is null) return [];

        if (keys.Count == 1)
            return ownedInternalNamesByKeyword.TryGetValue(keys[0], out var list) ? list : [];

        if (!ownedInternalNamesByKeyword.TryGetValue(keys[0], out var seedList)) return [];
        var seed = new HashSet<string>(seedList, StringComparer.Ordinal);
        for (var i = 1; i < keys.Count && seed.Count > 0; i++)
        {
            if (!ownedInternalNamesByKeyword.TryGetValue(keys[i], out var next)) return [];
            seed.IntersectWith(next);
        }
        return seed;
    }

    /// <summary>
    /// Walk the producer graph from <paramref name="seeds"/> down <paramref name="expansionDepth"/>
    /// levels, collecting every item a full expansion would touch. No shortfall filter — this is
    /// the stable plan skeleton that survives the demand pass clamping counts to zero. Cycle-safe
    /// because the chain set prevents re-enqueuing items we've already walked. Keyword-matched
    /// ingredient slots are added as leaves (they have no producer).
    /// </summary>
    private static ISet<string> BuildChainItems(
        IReadOnlyList<string> seeds,
        int expansionDepth,
        IReferenceDataService refData,
        IReadOnlyDictionary<string, Recipe> producers,
        Dictionary<string, KeywordSlot> keywordSlots)
    {
        var chain = new HashSet<string>(seeds, StringComparer.Ordinal);
        var frontier = new HashSet<string>(seeds, StringComparer.Ordinal);

        for (var level = 0; level < expansionDepth; level++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in frontier)
            {
                if (!producers.TryGetValue(item, out var recipe)) continue;
                foreach (var ingredient in recipe.Ingredients)
                {
                    switch (ingredient)
                    {
                        case RecipeItemIngredient itemIng:
                            if (!refData.Items.TryGetValue(itemIng.ItemCode, out var ing)) continue;
                            if (string.IsNullOrEmpty(ing.InternalName)) continue;
                            if (chain.Add(ing.InternalName!)) next.Add(ing.InternalName!);
                            break;
                        case RecipeKeywordIngredient kwIng:
                            var key = RecipeExpander.MakeKeywordKey(kwIng.ItemKeys);
                            keywordSlots.TryAdd(key, new KeywordSlot(kwIng.ItemKeys, kwIng.Desc));
                            chain.Add(key);
                            // Keyword rows are leaves — no recursion needed; they have no producer.
                            break;
                    }
                }
            }
            if (next.Count == 0) break;
            frontier = next;
        }

        return chain;
    }

    /// <summary>
    /// Crafting-step depth: 0 if the item has no producer (gathered / bought),
    /// otherwise 1 + max depth of its recipe's resolvable ingredients. Cycle-safe
    /// via <paramref name="onPath"/>. Keyword-matched ingredients contribute 0
    /// (they are always leaves).
    /// </summary>
    private static int ComputeDepth(
        string itemName,
        IReadOnlyDictionary<string, Recipe> producers,
        IReferenceDataService refData,
        Dictionary<string, int> cache,
        HashSet<string> onPath)
    {
        if (cache.TryGetValue(itemName, out var cached)) return cached;
        if (!producers.TryGetValue(itemName, out var recipe)) { cache[itemName] = 0; return 0; }
        if (!onPath.Add(itemName)) return 0;

        var maxIngredientDepth = 0;
        foreach (var ingredient in recipe.Ingredients)
        {
            if (ingredient is RecipeItemIngredient itemIng
                && refData.Items.TryGetValue(itemIng.ItemCode, out var item)
                && !string.IsNullOrEmpty(item.InternalName))
            {
                maxIngredientDepth = Math.Max(maxIngredientDepth, ComputeDepth(item.InternalName!, producers, refData, cache, onPath));
            }
            // Keyword-matched ingredients: depth 0 leaves, no contribution.
        }

        onPath.Remove(itemName);
        var depth = 1 + maxIngredientDepth;
        cache[itemName] = depth;
        return depth;
    }

    private static Item? FindPrimaryOutput(Recipe recipe, IReferenceDataService refData)
    {
        if (recipe.ResultItems is { } results)
            foreach (var result in results)
                if (refData.Items.TryGetValue(result.ItemCode, out var item)) return item;
        if (recipe.ProtoResultItems is { } proto)
            foreach (var result in proto)
                if (refData.Items.TryGetValue(result.ItemCode, out var item)) return item;
        return null;
    }
}
