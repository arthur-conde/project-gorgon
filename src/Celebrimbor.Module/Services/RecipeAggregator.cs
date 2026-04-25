using Celebrimbor.Domain;
using Mithril.Shared.Reference;

namespace Celebrimbor.Services;

/// <summary>
/// Pure aggregation logic. Given a craft list and an expansion depth, compute
/// the expected demand per item. ChanceToConsume is honoured as expected value,
/// so the returned TotalNeeded is the ceiling of the raw expectation.
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
        IReadOnlyDictionary<string, int>? overridesByInternalName = null)
    {
        var demand = new Dictionary<string, double>(StringComparer.Ordinal);
        var seeds = new List<string>();

        foreach (var entry in entries)
        {
            if (entry.Quantity <= 0) continue;
            if (!refData.RecipesByInternalName.TryGetValue(entry.RecipeInternalName, out var recipe)) continue;

            // Seed demand with the target recipe's *output* item, so it flows through
            // expansion/projection like any other row. Raw ingredients only appear when
            // expansionDepth > 0 pulls them in.
            var output = FindPrimaryOutput(recipe, refData);
            if (output is null) continue;
            demand[output.InternalName] = demand.TryGetValue(output.InternalName, out var existing)
                ? existing + entry.Quantity
                : entry.Quantity;
            seeds.Add(output.InternalName);
        }

        var producers = BuildProducerLookup(refData);

        // Chain pass: everything the plan touches at this depth, shortfall-agnostic.
        // Keeps the shopping list visible as a stable skeleton when the demand pass
        // collapses rows to zero (e.g. user overrides a target to match its needed count).
        var chainItems = BuildChainItems(seeds, expansionDepth, refData, producers);

        if (expansionDepth > 0)
            Expand(demand, expansionDepth, refData, producers, onHandByInternalName, overridesByInternalName);

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
            if (!refData.ItemsByInternalName.TryGetValue(itemName, out var item)) continue;

            var totalNeeded = (int)Math.Ceiling(expected);
            var detected = onHandByInternalName is not null && onHandByInternalName.TryGetValue(itemName, out var on) ? on : 0;
            int? overrideCount = overridesByInternalName is not null && overridesByInternalName.TryGetValue(itemName, out var ov) ? ov : null;
            var locations = locationsByInternalName is not null && locationsByInternalName.TryGetValue(itemName, out var locs) ? locs : [];
            var primaryTag = item.Keywords.FirstOrDefault()?.Tag ?? "Misc";
            var isAlsoRecipe = producers.ContainsKey(itemName);
            var depth = ComputeDepth(itemName, producers, refData, depthCache, new HashSet<string>(StringComparer.Ordinal));

            rows.Add(new AggregatedIngredient(
                ItemInternalName: itemName,
                ItemId: item.Id,
                DisplayName: item.Name,
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

    /// <summary>
    /// Walk the producer graph from <paramref name="seeds"/> down <paramref name="expansionDepth"/>
    /// levels, collecting every item a full expansion would touch. No shortfall filter — this is
    /// the stable plan skeleton that survives the demand pass clamping counts to zero. Cycle-safe
    /// because the chain set prevents re-enqueuing items we've already walked.
    /// </summary>
    private static ISet<string> BuildChainItems(
        IReadOnlyList<string> seeds,
        int expansionDepth,
        IReferenceDataService refData,
        IReadOnlyDictionary<string, RecipeEntry> producers)
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
                    if (!refData.Items.TryGetValue(ingredient.ItemCode, out var ing)) continue;
                    if (chain.Add(ing.InternalName)) next.Add(ing.InternalName);
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
    /// via <paramref name="onPath"/>.
    /// </summary>
    private static int ComputeDepth(
        string itemName,
        IReadOnlyDictionary<string, RecipeEntry> producers,
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
            if (!refData.Items.TryGetValue(ingredient.ItemCode, out var item)) continue;
            maxIngredientDepth = Math.Max(maxIngredientDepth, ComputeDepth(item.InternalName, producers, refData, cache, onPath));
        }

        onPath.Remove(itemName);
        var depth = 1 + maxIngredientDepth;
        cache[itemName] = depth;
        return depth;
    }

    private static void AddIngredients(
        Dictionary<string, double> demand,
        RecipeEntry recipe,
        double batches,
        IReferenceDataService refData)
    {
        foreach (var ingredient in recipe.Ingredients)
        {
            if (!refData.Items.TryGetValue(ingredient.ItemCode, out var item)) continue;
            var perBatch = ingredient.StackSize * (ingredient.ChanceToConsume ?? 1.0);
            if (perBatch <= 0) continue;
            var add = batches * perBatch;

            demand[item.InternalName] = demand.TryGetValue(item.InternalName, out var existing)
                ? existing + add
                : add;
        }
    }

    private static void Expand(
        Dictionary<string, double> demand,
        int depth,
        IReferenceDataService refData,
        IReadOnlyDictionary<string, RecipeEntry> producers,
        IReadOnlyDictionary<string, int>? onHandByInternalName,
        IReadOnlyDictionary<string, int>? overridesByInternalName)
    {
        // Intermediates stay in the output so the user still sees "3x Good Metal Slab".
        // alreadyExpanded guards against cycles (A → B → A) — once we've expanded a
        // craftable, we don't touch it again even if it reappears in demand later.
        var alreadyExpanded = new HashSet<string>(StringComparer.Ordinal);

        for (var level = 0; level < depth; level++)
        {
            var candidates = demand
                .Where(kv => kv.Value > 0
                             && !alreadyExpanded.Contains(kv.Key)
                             && producers.ContainsKey(kv.Key))
                .ToList();

            if (candidates.Count == 0) break;

            foreach (var (itemName, expected) in candidates)
            {
                alreadyExpanded.Add(itemName);

                var recipe = producers[itemName];
                var outputStackSize = FindOutputStackSize(recipe, itemName, refData);
                if (outputStackSize <= 0) continue;

                // Only the shortfall between demand and effective stock actually needs to be crafted.
                // A manual override (user-entered count) beats the detected on-hand reading so the
                // raw ingredient totals update live when the user adjusts an intermediate's count.
                var onHand = onHandByInternalName is not null
                    && onHandByInternalName.TryGetValue(itemName, out var stock) ? stock : 0;
                var effectiveStock = overridesByInternalName is not null
                    && overridesByInternalName.TryGetValue(itemName, out var ov) ? ov : onHand;
                var shortfall = expected - effectiveStock;
                if (shortfall <= 0) continue;

                var batches = shortfall / outputStackSize;
                AddIngredients(demand, recipe, batches, refData);
            }
        }

        foreach (var key in demand.Where(kv => kv.Value <= 0).Select(kv => kv.Key).ToList())
            demand.Remove(key);
    }

    /// <summary>
    /// Build a reverse lookup of "item InternalName → recipe that produces it" by
    /// scanning every recipe's ResultItems (and ProtoResultItems as a fallback).
    /// The item's own InternalName comes from items.json, which is what the demand
    /// dictionary is keyed by. This replaces the naive "does any recipe share this
    /// item's InternalName" match that only worked when recipe and item happened to
    /// share a name (e.g. Butter) and silently skipped most intermediate crafts.
    /// </summary>
    private static IReadOnlyDictionary<string, RecipeEntry> BuildProducerLookup(IReferenceDataService refData)
    {
        var map = new Dictionary<string, RecipeEntry>(StringComparer.Ordinal);
        foreach (var recipe in refData.Recipes.Values)
        {
            RegisterResults(recipe, recipe.ResultItems, refData, map);
            if (recipe.ProtoResultItems is { } proto)
                RegisterResults(recipe, proto, refData, map);
        }
        return map;
    }

    private static void RegisterResults(
        RecipeEntry recipe,
        IReadOnlyList<RecipeItemRef> results,
        IReferenceDataService refData,
        Dictionary<string, RecipeEntry> map)
    {
        foreach (var result in results)
        {
            if (!refData.Items.TryGetValue(result.ItemCode, out var item)) continue;
            // First recipe wins. If multiple recipes produce the same item, the
            // aggregator picks deterministically by enumeration order — stable across runs.
            map.TryAdd(item.InternalName, recipe);
        }
    }

    private static ItemEntry? FindPrimaryOutput(RecipeEntry recipe, IReferenceDataService refData)
    {
        foreach (var result in recipe.ResultItems)
            if (refData.Items.TryGetValue(result.ItemCode, out var item)) return item;
        if (recipe.ProtoResultItems is { } proto)
            foreach (var result in proto)
                if (refData.Items.TryGetValue(result.ItemCode, out var item)) return item;
        return null;
    }

    private static int FindOutputStackSize(RecipeEntry recipe, string itemInternalName, IReferenceDataService refData)
    {
        foreach (var result in recipe.ResultItems)
        {
            if (!refData.Items.TryGetValue(result.ItemCode, out var item)) continue;
            if (item.InternalName == itemInternalName) return Math.Max(1, result.StackSize);
        }
        // Fall back to the first result if the recipe doesn't publish the item
        // we expected (rare but possible with data drift).
        return recipe.ResultItems.FirstOrDefault()?.StackSize ?? 1;
    }
}
