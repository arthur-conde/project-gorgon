namespace Mithril.Reference.Models.Quests;

/// <summary>
/// Item reference used by <c>Rewards_Items</c>, <c>PreGiveItems</c>, and
/// <c>MidwayGiveItems</c>. The <see cref="Item"/> field is an item internal name
/// (resolvable against <c>items.json</c>); <see cref="StackSize"/> is the count.
/// </summary>
public sealed class QuestItemRef
{
    public string? Item { get; set; }
    public int StackSize { get; set; }
}
