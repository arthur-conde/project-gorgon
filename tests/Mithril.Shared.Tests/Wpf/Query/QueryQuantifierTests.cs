using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

/// <summary>
/// <c>&lt;col&gt; WITH ANY|ALL (&lt;pred&gt;)</c> quantified subqueries over a homogeneous
/// object collection (slice 1 — the v1 consumer's shape). The headline guarantee is the
/// <em>per-element correlation</em> test: a conjunction inside the parens must be satisfied
/// by a single element, not column-wise across the collection.
/// </summary>
public class QueryQuantifierTests
{
    private sealed record Cap(string Tier, int? GoldCap, IReadOnlyList<string> Keywords);

    private sealed record Holder(string Name, IReadOnlyList<Cap>? Caps, IReadOnlyList<string>? Tags);

    private static readonly Holder[] Dataset =
    {
        // Single element satisfies "Neutral AND Armor" together.
        new("together", [new("Neutral", 5000, ["Armor", "Weapon"])], ["x"]),
        // Neutral and Armor exist but on DIFFERENT elements — the correlation trap.
        new("split", [new("Neutral", 100, ["Potion"]), new("BestFriends", 9000, ["Armor"])], ["y"]),
        new("empty", [], []),
        new("nullcaps", null, null),
    };

    private static readonly IReadOnlyDictionary<string, ColumnBinding> Columns =
        new Dictionary<string, ColumnBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = new("name", typeof(string), r => ((Holder)r).Name),
            ["caps"] = new("caps", typeof(IReadOnlyList<Cap>), r => ((Holder)r).Caps),
            ["tags"] = new("tags", typeof(IReadOnlyList<string>), r => ((Holder)r).Tags),
        };

    private static string[] Filter(string query)
    {
        var predicate = QueryCompiler.Compile(query, Columns);
        if (predicate is null) return Dataset.Select(h => h.Name).ToArray();
        return Dataset.Where(h => predicate(h)).Select(h => h.Name).ToArray();
    }

    // ─────────────── parser ───────────────

    [Fact]
    public void Parses_WITH_ANY_into_QuantifiedNode()
    {
        var parsed = QueryParser.Parse("caps WITH ANY (Tier = 'Neutral')");
        var node = parsed!.Predicate.Should().BeOfType<QuantifiedNode>().Subject;
        node.Column.Should().Be("caps");
        node.Quantifier.Should().Be(Quantifier.Any);
        node.Inner.Should().BeOfType<ComparisonNode>();
    }

    [Fact]
    public void Parses_WITH_ALL_and_nested_conjunction()
    {
        var node = QueryParser.Parse("caps WITH ALL (Tier = 'Neutral' AND GoldCap > 100)")!
            .Predicate.Should().BeOfType<QuantifiedNode>().Subject;
        node.Quantifier.Should().Be(Quantifier.All);
        node.Inner.Should().BeOfType<AndNode>();
    }

    [Fact]
    public void Prefix_NOT_wraps_the_quantifier()
    {
        QueryParser.Parse("NOT (caps WITH ANY (Tier = 'Neutral'))")!
            .Predicate.Should().BeOfType<NotNode>()
            .Which.Inner.Should().BeOfType<QuantifiedNode>();
    }

    [Fact]
    public void Nested_quantifier_parses()
    {
        var outer = QueryParser.Parse("caps WITH ANY (Keywords WITH ALL (x = 'y'))");
        outer!.Predicate.Should().BeOfType<QuantifiedNode>()
            .Which.Inner.Should().BeOfType<QuantifiedNode>();
    }

    [Theory]
    [InlineData("caps WITH ANY")]                 // missing (
    [InlineData("caps WITH ANY ()")]              // empty predicate
    [InlineData("caps WITH (Tier = 'x')")]        // missing ANY/ALL
    [InlineData("caps NOT WITH ANY (Tier = 'x')")] // inline NOT not allowed
    public void Malformed_quantifier_throws(string query)
    {
        Action act = () => QueryParser.Parse(query);
        act.Should().Throw<QueryException>();
    }

    // ─────────────── compiler ───────────────

    [Fact]
    public void ANY_correlates_per_element_not_column_wise()
    {
        // The whole point: "Neutral AND GoldCap>=500" must be true for ONE element.
        // "together" has Neutral@5000; "split" has Neutral@100 and Armor@9000 separately.
        Filter("caps WITH ANY (Tier = 'Neutral' AND GoldCap >= 500)")
            .Should().BeEquivalentTo("together");
    }

    [Fact]
    public void ANY_true_when_one_element_matches()
    {
        Filter("caps WITH ANY (Keywords CONTAINS 'Armor')")
            .Should().BeEquivalentTo("together", "split");
    }

    [Fact]
    public void ALL_requires_every_element_and_is_vacuously_true_over_empty()
    {
        // "together" (one Neutral elem) passes; "split" has a BestFriends elem so fails;
        // "empty" has no elements → vacuously true; "nullcaps" (null) → false.
        Filter("caps WITH ALL (Tier = 'Neutral')")
            .Should().BeEquivalentTo("together", "empty");
    }

    [Fact]
    public void ANY_over_empty_or_null_is_false()
    {
        Filter("caps WITH ANY (GoldCap > 0)")
            .Should().BeEquivalentTo("together", "split");
    }

    [Fact]
    public void Prefix_NOT_negates_the_quantifier()
    {
        // NOT (caps WITH ANY Armor) → everything that does NOT have an Armor element.
        Filter("NOT (caps WITH ANY (Keywords CONTAINS 'Armor'))")
            .Should().BeEquivalentTo("empty", "nullcaps");
    }

    [Fact]
    public void Non_collection_column_rejects_quantifier()
    {
        Action act = () => QueryCompiler.Compile("name WITH ANY (Tier = 'x')", Columns);
        act.Should().Throw<QueryException>().WithMessage("*object-collection*");
    }

    [Fact]
    public void String_collection_rejects_quantifier_use_CONTAINS()
    {
        Action act = () => QueryCompiler.Compile("tags WITH ANY (x = 'y')", Columns);
        act.Should().Throw<QueryException>().WithMessage("*CONTAINS*");
    }
}
