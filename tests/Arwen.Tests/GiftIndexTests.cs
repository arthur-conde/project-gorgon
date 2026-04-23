using Arwen.Domain;
using Gorgon.Shared.Reference;
using FluentAssertions;
using Xunit;

namespace Arwen.Tests;

public sealed class GiftIndexTests
{
    private static IReadOnlyDictionary<long, ItemEntry> BuildItems(params (long Id, string Name, string[] Keywords)[] items)
        => BuildItemsWithValues(items.Select(i => (i.Id, i.Name, i.Keywords, 0m)).ToArray());

    private static IReadOnlyDictionary<long, ItemEntry> BuildItemsWithValues(params (long Id, string Name, string[] Keywords, decimal Value)[] items)
    {
        var dict = new Dictionary<long, ItemEntry>();
        foreach (var (id, name, keywords, value) in items)
        {
            var kws = keywords.Select(k =>
            {
                var eq = k.IndexOf('=');
                return eq > 0 && int.TryParse(k.AsSpan(eq + 1), out var q)
                    ? new ItemKeyword(k[..eq], q)
                    : new ItemKeyword(k, 0);
            }).ToList();
            dict[id] = new ItemEntry(id, name, name, 1, 0, kws, Value: value);
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, NpcEntry> BuildNpcs(params (string Key, string Name, (string Desire, string[] Keywords, double Pref)[] Prefs)[] npcs)
    {
        var dict = new Dictionary<string, NpcEntry>(StringComparer.Ordinal);
        foreach (var (key, name, prefs) in npcs)
        {
            var prefList = prefs.Select(p => new NpcPreference(
                p.Desire,
                p.Keywords,
                string.Join(", ", p.Keywords),
                p.Pref,
                null)).ToList();
            dict[key] = new NpcEntry(key, name, "TestArea", prefList, ["Friends"], []);
        }
        return dict;
    }

    [Fact]
    public void MatchesItemToNpcPreference()
    {
        var items = BuildItems(
            (1, "Veggie Stew", ["VegetarianDish=84", "Food"]),
            (2, "Sword", ["Weapon"]));
        var npcs = BuildNpcs(
            ("NPC_Test", "Test NPC", [("Love", new[] { "VegetarianDish" }, 3.5)]));

        var index = new GiftIndex();
        index.Build(items, npcs);

        var gifts = index.GetGiftsForNpc("NPC_Test");
        gifts.Should().HaveCount(1);
        gifts[0].ItemId.Should().Be(1);
        gifts[0].Desire.Should().Be("Love");
        gifts[0].Pref.Should().Be(3.5);
    }

    [Fact]
    public void HigherPrefWins_WhenItemMatchesMultiplePreferences()
    {
        var items = BuildItems(
            (1, "Fancy Food", ["VegetarianDish=100", "Food=50"]));
        var npcs = BuildNpcs(
            ("NPC_Test", "Test NPC", [
                ("Love", new[] { "VegetarianDish" }, 2.0),
                ("Like", new[] { "Food" }, 5.0)]));

        var index = new GiftIndex();
        index.Build(items, npcs);

        var gifts = index.GetGiftsForNpc("NPC_Test");
        gifts.Should().HaveCount(1);
        // Same item, same Value → higher pref (5.0 Food) wins the RelativeScore
        gifts[0].Pref.Should().Be(5.0);
    }

    [Fact]
    public void NoMatch_ReturnsEmpty()
    {
        var items = BuildItems((1, "Sword", ["Weapon"]));
        var npcs = BuildNpcs(("NPC_Test", "Test NPC", [("Love", new[] { "Food" }, 3.0)]));

        var index = new GiftIndex();
        index.Build(items, npcs);

        index.GetGiftsForNpc("NPC_Test").Should().BeEmpty();
    }

    [Fact]
    public void UnknownNpc_ReturnsEmpty()
    {
        var index = new GiftIndex();
        index.Build(new Dictionary<long, ItemEntry>(), new Dictionary<string, NpcEntry>());
        index.GetGiftsForNpc("NPC_Unknown").Should().BeEmpty();
    }

    [Fact]
    public void MatchItemToNpc_FindsSpecificItem()
    {
        var items = BuildItemsWithValues(
            (1, "Food A", ["Food"], 10m),
            (2, "Food B", ["Food"], 50m));
        var npcs = BuildNpcs(
            ("NPC_Test", "Test NPC", [("Love", new[] { "Food" }, 2.0)]));

        var index = new GiftIndex();
        index.Build(items, npcs);

        var match = index.MatchItemToNpc(2, "NPC_Test");
        match.Should().NotBeNull();
        match!.Pref.Should().Be(2.0);
        match.ItemValue.Should().Be(50);
    }

    [Fact]
    public void SortedByRelativeScoreDescending()
    {
        var items = BuildItemsWithValues(
            (1, "Low", ["Food"], 10m),
            (2, "High", ["Food"], 100m),
            (3, "Mid", ["Food"], 50m));
        var npcs = BuildNpcs(
            ("NPC_Test", "Test NPC", [("Love", new[] { "Food" }, 2.0)]));

        var index = new GiftIndex();
        index.Build(items, npcs);

        var gifts = index.GetGiftsForNpc("NPC_Test");
        gifts.Should().HaveCount(3);
        gifts[0].ItemId.Should().Be(2); // 2.0 * 100 = 200
        gifts[1].ItemId.Should().Be(3); // 2.0 * 50 = 100
        gifts[2].ItemId.Should().Be(1); // 2.0 * 10 = 20
    }

    [Fact]
    public void MultiKeyword_RequiresAllKeywordsPresent()
    {
        // NPC wants items with BOTH "SkillPrereq:Archery" AND "Loot"
        var items = BuildItems(
            (1, "Archer Bow", ["Equipment", "Loot", "SkillPrereq:Archery"]),    // matches: has both
            (2, "Basic Bow", ["Equipment", "SkillPrereq:Archery"]),              // no Loot → no match
            (3, "Random Loot", ["Loot"]));                                        // no SkillPrereq → no match
        var npcs = BuildNpcs(
            ("NPC_Test", "Test NPC", [("Love", new[] { "SkillPrereq:Archery", "Loot" }, 2.0)]));

        var index = new GiftIndex();
        index.Build(items, npcs);

        var gifts = index.GetGiftsForNpc("NPC_Test");
        gifts.Should().HaveCount(1);
        gifts[0].ItemId.Should().Be(1);
        gifts[0].ItemName.Should().Be("Archer Bow");
    }

    [Fact]
    public void Rebuild_RaisesEvent()
    {
        var index = new GiftIndex();
        var raised = false;
        index.Rebuilt += (_, _) => raised = true;

        index.Build(new Dictionary<long, ItemEntry>(), new Dictionary<string, NpcEntry>());
        raised.Should().BeTrue();
    }
}
