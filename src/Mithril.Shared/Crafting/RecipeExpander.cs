using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Crafting;

/// <summary>
/// A keyword-matched ingredient slot ("any item whose keywords include every listed
/// tag"). Surfaced as a synthetic demand key (<see cref="RecipeExpander.MakeKeywordKey"/>)
/// so it flows through expansion like a concrete item; the caller resolves it.
/// </summary>
public readonly record struct KeywordSlot(IReadOnlyList<string> Keys, string? Desc);

/// <summary>
/// Pure, demand-driven recipe expander (#226, supersedes #121). "I need N units of
/// item X → pick a recipe that produces X → for each ingredient, recurse on the
/// shortfall vs. on-hand." Cycle-safe (visited-set) and depth-capped.
///
/// Multi-output recipes credit <b>all</b> of the chosen recipe's outputs against
/// outstanding demand — deterministic outputs at full stack, random outputs
/// (<see cref="RecipeResultItem.PercentChance"/>) at expected value — so an
/// independently-demanded byproduct isn't crafted twice (closes #42). Variance is
/// handled at execution time by the plan-aware craft list (#228), not modelled
/// probabilistically here.
///
/// Optimisation (which alternative to pick) and presentation (how to render the
/// tree) are deliberately *not* here — the caller owns both via
/// <see cref="RecipeChoicePolicy"/>.
/// </summary>
public sealed class RecipeExpander
{
    public const string KeywordKeyPrefix = "#keys:";

    private readonly IReferenceDataService _ref;

    public RecipeExpander(IReferenceDataService referenceData, RecipeProducerIndex? producers = null)
    {
        _ref = referenceData ?? throw new ArgumentNullException(nameof(referenceData));
        Producers = producers ?? RecipeProducerIndex.Build(referenceData);
    }

    public RecipeProducerIndex Producers { get; }

    /// <summary>
    /// Synthetic demand key for a keyword-matched ingredient slot. Single-key slots
    /// key on the bare tag; multi-key slots on the ordinal-sorted comma-joined list
    /// so two recipes citing the same set collapse to one demand row.
    /// </summary>
    public static string MakeKeywordKey(IReadOnlyList<string> keys)
    {
        if (keys.Count == 1) return KeywordKeyPrefix + keys[0];
        var ordered = keys.OrderBy(k => k, StringComparer.Ordinal);
        return KeywordKeyPrefix + string.Join(",", ordered);
    }

    public static bool IsKeywordKey(string demandKey)
        => demandKey.StartsWith(KeywordKeyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Per-craft produced quantity of <paramref name="result"/>: full
    /// <see cref="RecipeResultItem.StackSize"/> when deterministic, or the
    /// expected value <c>StackSize × PercentChance/100</c> when random.
    /// </summary>
    public static double ExpectedYield(RecipeResultItem result)
    {
        var stack = Math.Max(1, result.StackSize);
        if (result.PercentChance is not { } pct || pct >= 100.0) return stack;
        if (pct <= 0.0) return 0.0;
        return stack * (pct / 100.0);
    }

    /// <summary>
    /// Stack size of the slot in <paramref name="recipe"/> that yields
    /// <paramref name="itemInternalName"/>, falling back to the first result on
    /// data drift. Deterministic value (no expected-value scaling) — used to size
    /// "how many batches cover this shortfall".
    /// </summary>
    public int OutputStackSize(Recipe recipe, string itemInternalName)
    {
        if (recipe.ResultItems is { } results)
        {
            foreach (var result in results)
            {
                if (!_ref.Items.TryGetValue(result.ItemCode, out var item)) continue;
                if (item.InternalName == itemInternalName) return Math.Max(1, result.StackSize);
            }
            if (results.Count > 0) return Math.Max(1, results[0].StackSize);
        }
        if (recipe.ProtoResultItems is { } proto)
        {
            foreach (var result in proto)
            {
                if (!_ref.Items.TryGetValue(result.ItemCode, out var item)) continue;
                if (item.InternalName == itemInternalName) return Math.Max(1, result.StackSize);
            }
        }
        return 1;
    }

    /// <summary>
    /// The recipe's deterministic-or-expected outputs as (itemInternalName → per-batch
    /// yield) pairs. Used to credit byproducts against demand.
    /// </summary>
    private IEnumerable<(string InternalName, double PerBatch)> OutputsOf(Recipe recipe)
    {
        IEnumerable<RecipeResultItem> Results()
        {
            if (recipe.ResultItems is { } r) foreach (var x in r) yield return x;
            if (recipe.ProtoResultItems is { } p) foreach (var x in p) yield return x;
        }

        foreach (var result in Results())
        {
            if (!_ref.Items.TryGetValue(result.ItemCode, out var item)) continue;
            if (string.IsNullOrEmpty(item.InternalName)) continue;
            var perBatch = ExpectedYield(result);
            if (perBatch > 0) yield return (item.InternalName!, perBatch);
        }
    }

    /// <summary>
    /// Add <paramref name="recipe"/>'s ingredient demand for <paramref name="batches"/>
    /// batches. Item slots key on item <c>InternalName</c>; keyword slots on a
    /// synthetic key recorded into <paramref name="keywordSlots"/>.
    /// </summary>
    public void AddIngredients(
        IDictionary<string, double> demand,
        Recipe recipe,
        double batches,
        IDictionary<string, KeywordSlot> keywordSlots)
    {
        foreach (var ingredient in recipe.Ingredients)
        {
            string demandKey;
            switch (ingredient)
            {
                case RecipeItemIngredient itemIng:
                    if (!_ref.Items.TryGetValue(itemIng.ItemCode, out var item)) continue;
                    if (string.IsNullOrEmpty(item.InternalName)) continue;
                    demandKey = item.InternalName!;
                    break;
                case RecipeKeywordIngredient kwIng:
                    demandKey = MakeKeywordKey(kwIng.ItemKeys);
                    if (!keywordSlots.ContainsKey(demandKey))
                        keywordSlots[demandKey] = new KeywordSlot(kwIng.ItemKeys, kwIng.Desc);
                    break;
                default:
                    continue;
            }

            var perBatch = ingredient.StackSize * (ingredient.ChanceToConsume ?? 1.0);
            if (perBatch <= 0) continue;
            var add = batches * perBatch;

            demand[demandKey] = demand.TryGetValue(demandKey, out var existing) ? existing + add : add;
        }
    }

    /// <summary>
    /// Demand-driven expansion, mutating <paramref name="demand"/> in place.
    /// Intermediates stay in the map (so the caller can still show "3× Slab"); a
    /// per-expansion visited-set guards cycles (A→B→A) and the
    /// <paramref name="maxDepth"/> cap bounds recursion. Each chosen recipe credits
    /// all its outputs against any independently-outstanding demand (the #42 fix).
    /// </summary>
    public void Expand(
        IDictionary<string, double> demand,
        int maxDepth,
        IReadOnlyDictionary<string, int>? onHandByInternalName,
        IReadOnlyDictionary<string, int>? overridesByInternalName,
        IDictionary<string, KeywordSlot> keywordSlots,
        RecipeChoicePolicy? choose = null)
    {
        var alreadyExpanded = new HashSet<string>(StringComparer.Ordinal);

        for (var level = 0; level < maxDepth; level++)
        {
            var candidates = demand
                .Where(kv => kv.Value > 0
                             && !alreadyExpanded.Contains(kv.Key)
                             && Producers.HasProducer(kv.Key))
                .Select(kv => kv.Key)
                .ToList();

            if (candidates.Count == 0) break;

            foreach (var itemName in candidates)
            {
                alreadyExpanded.Add(itemName);

                var alternatives = Producers.Alternatives(itemName);
                if (alternatives.Count == 0) continue;
                var recipe = choose is not null
                    ? choose(itemName, alternatives)
                    : alternatives[0];

                var outputStackSize = OutputStackSize(recipe, itemName);
                if (outputStackSize <= 0) continue;

                var expected = demand.TryGetValue(itemName, out var d) ? d : 0;

                // Only the shortfall between demand and effective stock needs crafting.
                // A manual override beats the detected on-hand reading so raw totals
                // update live when the user adjusts an intermediate's count.
                var onHand = onHandByInternalName is not null
                    && onHandByInternalName.TryGetValue(itemName, out var stock) ? stock : 0;
                var effectiveStock = overridesByInternalName is not null
                    && overridesByInternalName.TryGetValue(itemName, out var ov) ? ov : onHand;
                var shortfall = expected - effectiveStock;
                if (shortfall <= 0) continue;

                var batches = shortfall / outputStackSize;

                // #42: a multi-output recipe also yields its *other* outputs. Credit
                // them against any independently-outstanding demand so we don't craft
                // a shared byproduct twice. Single-output recipes have no siblings —
                // behaviour is unchanged for them.
                foreach (var (outName, perBatch) in OutputsOf(recipe))
                {
                    if (string.Equals(outName, itemName, StringComparison.Ordinal)) continue;
                    if (!demand.TryGetValue(outName, out var outstanding) || outstanding <= 0) continue;
                    var produced = batches * perBatch;
                    demand[outName] = Math.Max(0, outstanding - produced);
                }

                AddIngredients(demand, recipe, batches, keywordSlots);
            }
        }

        foreach (var key in demand.Where(kv => kv.Value <= 0).Select(kv => kv.Key).ToList())
            demand.Remove(key);
    }
}
