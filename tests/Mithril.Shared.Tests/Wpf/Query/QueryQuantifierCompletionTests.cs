using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mithril.Reference.Models.Quests;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

/// <summary>
/// Slice 3: completion + highlighter context-awareness for <c>WITH ANY|ALL</c>.
/// WITH/ANY/ALL highlight as keywords; an object-collection column offers the
/// quantifier instead of scalar operators; inside the block the caret completes
/// against the element sub-schema; closing the block (and any nested grouping)
/// restores the outer schema.
/// </summary>
public class QueryQuantifierCompletionTests
{
    private sealed record Cap(string Tier, int? GoldCap, IReadOnlyList<string> Keywords);

    private static readonly IReadOnlyList<ColumnSchema> Columns = new[]
    {
        new ColumnSchema("name", typeof(string), IsNullable: false),
        new ColumnSchema("caps", typeof(IReadOnlyList<Cap>), IsNullable: true),
        new ColumnSchema("reqs", typeof(IReadOnlyList<QuestRequirement>), IsNullable: true),
    };

    private static List<string> Labels(string query, int caret)
        => QueryCompletionProvider.Suggest(query, caret, Columns).Select(r => r.Label).ToList();

    private static HighlightKind At(string query, int pos)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "caps", "reqs", "name" };
        var span = QueryHighlighter.Highlight(query, known)
            .Single(s => pos >= s.Start && pos < s.Start + s.Length);
        return span.Kind;
    }

    // ─────────────── highlighter ───────────────

    [Fact]
    public void With_any_all_highlight_as_keyword()
    {
        const string q = "caps WITH ANY (Tier = 'x')";
        At(q, q.IndexOf("WITH", StringComparison.Ordinal)).Should().Be(HighlightKind.Keyword);
        At(q, q.IndexOf("ANY", StringComparison.Ordinal)).Should().Be(HighlightKind.Keyword);
        At("caps WITH ALL (Tier = 'x')", 10).Should().Be(HighlightKind.Keyword); // 'ALL'
    }

    // ─────────────── completion ───────────────

    [Fact]
    public void Object_collection_column_offers_quantifier_not_scalar_ops()
    {
        var labels = Labels("caps ", 5);
        labels.Should().Contain(new[] { "WITH ANY (", "WITH ALL (" });
        // Scalar/string operators must NOT be offered for a collection column.
        labels.Should().NotContain(new[] { "=", "<", "BETWEEN", "CONTAINS" });
        // caps is nullable → IS [NOT] NULL still valid.
        labels.Should().Contain(new[] { "IS NULL", "IS NOT NULL" });
    }

    [Fact]
    public void Inside_block_completes_against_homogeneous_element_schema()
    {
        var labels = Labels("caps WITH ANY (", 15);
        labels.Should().Contain(new[] { "Tier", "GoldCap", "Keywords" });
        labels.Should().NotContain("name"); // outer column is out of scope here
    }

    [Fact]
    public void Inside_block_polymorphic_offers_union_props_and_discriminator()
    {
        var labels = Labels("reqs WITH ANY (", 15);
        labels.Should().Contain("T");          // discriminator pseudo-column
        labels.Should().Contain("Level");      // colliding union prop
        labels.Should().Contain("Skill");      // subtype prop
        labels.Should().NotContain("caps");
    }

    [Fact]
    public void Closing_block_restores_outer_schema()
    {
        // After the matching ) the caret is back at row scope: OR → expect a column,
        // and that column list is the OUTER schema, not the element schema.
        var labels = Labels("caps WITH ANY (Tier = 'x') OR ", 30);
        labels.Should().Contain(new[] { "name", "caps" });
        labels.Should().NotContain("Tier");
    }

    [Fact]
    public void Nested_grouping_paren_does_not_leak_scope()
    {
        // Grouping parens inside the block keep the element schema; the block's own
        // close still pops correctly afterwards.
        const string q = "caps WITH ANY ((Tier = 'a') AND ";
        var inside = Labels(q, q.Length);
        inside.Should().Contain(new[] { "Tier", "GoldCap" });
        inside.Should().NotContain("name");
    }
}
