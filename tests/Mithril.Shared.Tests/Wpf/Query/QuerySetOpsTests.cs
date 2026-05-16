using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

/// <summary>
/// #375: collection cardinality grammar — <c>IS [NOT] EMPTY</c> and the
/// <c>WITH NONE (…)</c> quantifier. Locks: null collection counts as empty;
/// non-collection column is a compile error; <c>IS EMPTY</c> composes inside a
/// <c>WITH ANY</c> element schema (the motivating case); <c>WITH NONE</c> ≡
/// <c>NOT (WITH ANY)</c> incl. vacuous-true over empty/null.
/// </summary>
public class QuerySetOpsTests
{
    private sealed record Cap(string Tier, IReadOnlyList<string> Keywords);

    private sealed record Holder(string Name, IReadOnlyList<Cap>? Caps, IReadOnlyList<string>? Tags);

    private static readonly Holder[] Dataset =
    {
        new("emptytags", [new("Neutral", [])], []),                 // Tags=[]   ; a cap w/ empty Keywords
        new("hastags",   [new("Neutral", ["Armor"])], ["x"]),       // Tags=["x"]; cap w/ Keywords
        new("nulltags",  null, null),                               // both null
        new("mixedcaps", [new("A", ["Armor"]), new("B", [])], ["y"]), // one cap empty-kw, one not
    };

    private static readonly IReadOnlyDictionary<string, ColumnBinding> Columns =
        new Dictionary<string, ColumnBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = new("name", typeof(string), r => ((Holder)r).Name),
            ["caps"] = new("caps", typeof(IReadOnlyList<Cap>), r => ((Holder)r).Caps),
            ["tags"] = new("tags", typeof(IReadOnlyList<string>), r => ((Holder)r).Tags),
        };

    private static string[] Filter(string q)
    {
        var p = QueryCompiler.Compile(q, Columns);
        return p is null ? [] : Dataset.Where(h => p(h)).Select(h => h.Name).ToArray();
    }

    // ─────────── parser ───────────

    [Fact]
    public void Parses_IS_EMPTY_and_IS_NOT_EMPTY()
    {
        QueryParser.Parse("tags IS EMPTY")!.Predicate
            .Should().BeOfType<IsEmptyNode>().Which.Negated.Should().BeFalse();
        QueryParser.Parse("tags IS NOT EMPTY")!.Predicate
            .Should().BeOfType<IsEmptyNode>().Which.Negated.Should().BeTrue();
    }

    [Fact]
    public void IS_NULL_still_parses_to_IsNullNode_not_IsEmpty()
    {
        QueryParser.Parse("tags IS NULL")!.Predicate.Should().BeOfType<IsNullNode>();
        QueryParser.Parse("tags IS NOT NULL")!.Predicate
            .Should().BeOfType<IsNullNode>().Which.Negated.Should().BeTrue();
    }

    [Fact]
    public void Parses_WITH_NONE_into_QuantifiedNode()
    {
        QueryParser.Parse("caps WITH NONE (Tier = 'A')")!.Predicate
            .Should().BeOfType<QuantifiedNode>().Which.Quantifier.Should().Be(Quantifier.None);
    }

    [Theory]
    [InlineData("tags IS")]                 // neither NULL nor EMPTY
    [InlineData("tags IS NOT")]             // dangling
    [InlineData("caps WITH FOO (Tier='A')")] // bad quantifier
    [InlineData("caps WITH NONE")]          // missing (
    [InlineData("caps WITH NONE ()")]       // empty predicate
    public void Malformed_throws(string q)
    {
        Action act = () => QueryParser.Parse(q);
        act.Should().Throw<QueryException>();
    }

    // ─────────── compiler: IS [NOT] EMPTY ───────────

    [Fact]
    public void IS_EMPTY_true_for_empty_list_and_null_collection()
        => Filter("tags IS EMPTY").Should().BeEquivalentTo("emptytags", "nulltags");

    [Fact]
    public void IS_NOT_EMPTY_is_the_inverse()
        => Filter("tags IS NOT EMPTY").Should().BeEquivalentTo("hastags", "mixedcaps");

    [Fact]
    public void IS_EMPTY_on_a_non_collection_column_is_a_compile_error()
    {
        Action act = () => QueryCompiler.Compile("name IS EMPTY", Columns);
        act.Should().Throw<QueryException>().WithMessage("*collection*");
    }

    [Fact]
    public void IS_EMPTY_composes_inside_WITH_ANY_element_schema()
    {
        // The motivating #375 query shape: a cap row whose Keywords is empty.
        Filter("caps WITH ANY (Keywords IS EMPTY)")
            .Should().BeEquivalentTo("emptytags", "mixedcaps");
    }

    // ─────────── compiler: WITH NONE ───────────

    [Fact]
    public void WITH_NONE_matches_when_no_element_satisfies_and_is_vacuous_over_empty_or_null()
    {
        // No cap with empty Keywords: hastags (Armor only); nulltags (null → none
        // vacuously true). emptytags/mixedcaps each have an empty-Keywords cap.
        Filter("caps WITH NONE (Keywords IS EMPTY)")
            .Should().BeEquivalentTo("hastags", "nulltags");
    }

    [Fact]
    public void WITH_NONE_equals_NOT_WITH_ANY()
    {
        var none = Filter("caps WITH NONE (Tier = 'A')");
        var notAny = Filter("NOT (caps WITH ANY (Tier = 'A'))");
        none.Should().BeEquivalentTo(notAny);
    }

    // ─────────── highlighter / completion ───────────

    [Fact]
    public void EMPTY_and_NONE_highlight_as_keyword()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tags", "caps" };
        HighlightKind At(string q, int pos) => QueryHighlighter
            .Highlight(q, known).Single(s => pos >= s.Start && pos < s.Start + s.Length).Kind;

        const string e = "tags IS NOT EMPTY";
        At(e, e.IndexOf("EMPTY", StringComparison.Ordinal)).Should().Be(HighlightKind.Keyword);
        const string n = "caps WITH NONE (Tier = 'A')";
        At(n, n.IndexOf("NONE", StringComparison.Ordinal)).Should().Be(HighlightKind.Keyword);
    }

    [Fact]
    public void Object_collection_column_offers_NONE_and_IS_EMPTY()
    {
        var schema = new[]
        {
            new ColumnSchema("name", typeof(string), IsNullable: false),
            new ColumnSchema("caps", typeof(IReadOnlyList<Cap>), IsNullable: true),
        };
        var labels = QueryCompletionProvider.Suggest("caps ", 5, schema)
            .Select(r => r.Label).ToList();
        labels.Should().Contain(new[] { "WITH ANY (", "WITH ALL (", "WITH NONE (", "IS EMPTY", "IS NOT EMPTY" });
    }
}
