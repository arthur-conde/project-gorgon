using System.Collections.Generic;

namespace Mithril.Reference.Models.Items;

/// <summary>
/// One entry in an item's <c>Behaviors</c> array — describes a "use" action
/// the player can take with the item from inventory (e.g. "Drink", "Plant",
/// "Empty Bottle"). No discriminator field; all entries share the same shape
/// with optional fields per behaviour.
/// </summary>
public sealed class ItemBehavior
{
    public string? UseVerb { get; set; }

    /// <summary>Float in 655 entries, int in 130; modelled as double for tolerance.</summary>
    public double? UseDelay { get; set; }

    public string? UseDelayAnimation { get; set; }
    public string? UseAnimation { get; set; }

    /// <summary>Predicate keywords gating the use action (e.g. <c>"InWater"</c>).</summary>
    public IReadOnlyList<string>? UseRequirements { get; set; }

    public int? MetabolismCost { get; set; }
    public int? MinStackSizeNeeded { get; set; }
}
