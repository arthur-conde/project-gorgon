using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class ItemKeywordIndexTests
{
    private static ItemEntry Item(long id, string name, params string[] keywords) =>
        new(id, name, name, MaxStackSize: 50, IconId: 0,
            Keywords: keywords.Select(k => new ItemKeyword(k, 0)).ToList());

    private static ItemKeywordIndex Build(params ItemEntry[] items) =>
        new(items.ToDictionary(i => i.Id));

    [Fact]
    public void Single_key_returns_all_items_with_that_tag()
    {
        var idx = Build(
            Item(1, "Rough", "Crystal", "Material"),
            Item(2, "Polished", "Crystal", "Refined"),
            Item(3, "Wood", "Material"));

        idx.ItemsMatching(["Crystal"]).Select(i => i.Id).Should().BeEquivalentTo([1L, 2L]);
    }

    [Fact]
    public void Multi_key_intersects_AND_match()
    {
        var idx = Build(
            Item(1, "Hammer", "Crystal"),
            Item(2, "Sword", "Crystal", "EquipmentSlot:MainHand"),
            Item(3, "Shield", "EquipmentSlot:MainHand"));

        idx.ItemsMatching(["Crystal", "EquipmentSlot:MainHand"])
            .Select(i => i.Id).Should().BeEquivalentTo([2L]);
    }

    [Fact]
    public void Empty_keys_returns_empty()
    {
        var idx = Build(Item(1, "Anything", "Crystal"));
        idx.ItemsMatching([]).Should().BeEmpty();
    }

    [Fact]
    public void Unknown_key_returns_empty()
    {
        var idx = Build(Item(1, "Crystal", "Crystal"));
        idx.ItemsMatching(["Nonexistent"]).Should().BeEmpty();
        idx.ByKeyword("Nonexistent").Should().BeEmpty();
    }

    [Fact]
    public void Empty_index_returns_empty_for_any_query()
    {
        ItemKeywordIndex.Empty.ItemsMatching(["Crystal"]).Should().BeEmpty();
        ItemKeywordIndex.Empty.ByKeyword("Crystal").Should().BeEmpty();
    }

    [Fact]
    public void Humanise_joins_keys_with_plus()
    {
        ItemKeywordIndex.Humanise(["Crystal"]).Should().Be("Crystal");
        ItemKeywordIndex.Humanise(["Eye", "Fresh"]).Should().Be("Eye + Fresh");
    }
}
