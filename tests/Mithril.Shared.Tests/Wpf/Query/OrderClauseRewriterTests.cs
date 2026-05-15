using System.Collections.Generic;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

public class OrderClauseRewriterTests
{
    [Fact]
    public void Empty_input_with_order_produces_order_only()
    {
        OrderClauseRewriter.Rewrite("",
            new[] { new OrderSpec("Cost", OrderDirection.Descending) })
            .Should().Be("ORDER BY Cost DESC");
    }

    [Fact]
    public void Empty_order_strips_clause_preserving_predicate()
    {
        OrderClauseRewriter.Rewrite("Cost > 10 ORDER BY Name", System.Array.Empty<OrderSpec>())
            .Should().Be("Cost > 10");
    }

    [Fact]
    public void Empty_order_on_input_without_order_is_noop()
    {
        OrderClauseRewriter.Rewrite("Cost > 10", System.Array.Empty<OrderSpec>())
            .Should().Be("Cost > 10");
    }

    [Fact]
    public void Predicate_with_existing_order_gets_order_replaced()
    {
        OrderClauseRewriter.Rewrite("Cost > 10 ORDER BY Name",
            new[] { new OrderSpec("Cost", OrderDirection.Descending) })
            .Should().Be("Cost > 10 ORDER BY Cost DESC");
    }

    [Fact]
    public void Sort_by_alias_is_normalised_to_order_by()
    {
        OrderClauseRewriter.Rewrite("Cost > 10 SORT BY Name",
            new[] { new OrderSpec("Cost", OrderDirection.Ascending) })
            .Should().Be("Cost > 10 ORDER BY Cost");
    }

    [Fact]
    public void Ascending_direction_is_implicit_in_output()
    {
        OrderClauseRewriter.Rewrite("",
            new[] { new OrderSpec("Cost", OrderDirection.Ascending) })
            .Should().Be("ORDER BY Cost");
    }

    [Fact]
    public void Multi_key_emits_comma_separated()
    {
        OrderClauseRewriter.Rewrite("",
            new[]
            {
                new OrderSpec("Cost", OrderDirection.Descending),
                new OrderSpec("Name", OrderDirection.Ascending),
            })
            .Should().Be("ORDER BY Cost DESC, Name");
    }

    [Fact]
    public void Preserves_predicate_whitespace_when_replacing_order()
    {
        OrderClauseRewriter.Rewrite("  Cost > 10  ORDER BY Name",
            new[] { new OrderSpec("Cost", OrderDirection.Ascending) })
            .Should().Be("  Cost > 10  ORDER BY Cost");
    }
}
