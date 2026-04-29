namespace Mithril.Reference.Models.Abilities;

/// <summary>One entry in an ability's <c>Costs</c> array — currency required to use the ability.</summary>
public sealed class AbilityCost
{
    public string? Currency { get; set; }
    public int Price { get; set; }
}
