using System.Linq;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

public class QueryParserOrderTests
{
    [Fact]
    public void Empty_query_returns_null()
    {
        QueryParser.Parse("").Should().BeNull();
        QueryParser.Parse("   ").Should().BeNull();
        QueryParser.Parse(null!).Should().BeNull();
    }

    [Fact]
    public void Predicate_only_query_has_no_order()
    {
        var parsed = QueryParser.Parse("Cost > 10");
        parsed.Should().NotBeNull();
        parsed!.Predicate.Should().BeOfType<ComparisonNode>();
        parsed.Order.Should().BeEmpty();
    }

    [Fact]
    public void Order_only_query_has_no_predicate()
    {
        var parsed = QueryParser.Parse("ORDER BY Name");
        parsed.Should().NotBeNull();
        parsed!.Predicate.Should().BeNull();
        parsed.Order.Should().HaveCount(1);
        parsed.Order[0].Column.Should().Be("Name");
        parsed.Order[0].Direction.Should().Be(OrderDirection.Ascending);
    }

    [Fact]
    public void Order_default_direction_is_ascending()
    {
        var parsed = QueryParser.Parse("ORDER BY Cost");
        parsed!.Order[0].Direction.Should().Be(OrderDirection.Ascending);
    }

    [Fact]
    public void Order_direction_keywords_parse()
    {
        QueryParser.Parse("ORDER BY Cost ASC")!.Order[0].Direction.Should().Be(OrderDirection.Ascending);
        QueryParser.Parse("ORDER BY Cost ASCENDING")!.Order[0].Direction.Should().Be(OrderDirection.Ascending);
        QueryParser.Parse("ORDER BY Cost DESC")!.Order[0].Direction.Should().Be(OrderDirection.Descending);
        QueryParser.Parse("ORDER BY Cost DESCENDING")!.Order[0].Direction.Should().Be(OrderDirection.Descending);
    }

    [Fact]
    public void Sort_by_is_alias_for_order_by()
    {
        var parsed = QueryParser.Parse("SORT BY Cost DESC");
        parsed!.Order.Should().HaveCount(1);
        parsed.Order[0].Column.Should().Be("Cost");
        parsed.Order[0].Direction.Should().Be(OrderDirection.Descending);
    }

    [Fact]
    public void Multi_key_order_parses_in_text_order()
    {
        var parsed = QueryParser.Parse("ORDER BY Cost DESC, Name, Power ASC");
        parsed!.Order.Select(o => (o.Column, o.Direction)).Should().Equal(
            ("Cost", OrderDirection.Descending),
            ("Name", OrderDirection.Ascending),
            ("Power", OrderDirection.Ascending));
    }

    [Fact]
    public void Predicate_and_order_combine()
    {
        var parsed = QueryParser.Parse("Cost > 10 ORDER BY Name DESC");
        parsed!.Predicate.Should().BeOfType<ComparisonNode>();
        parsed.Order.Should().HaveCount(1);
        parsed.Order[0].Column.Should().Be("Name");
        parsed.Order[0].Direction.Should().Be(OrderDirection.Descending);
    }

    [Fact]
    public void Order_without_column_throws()
    {
        var act = () => QueryParser.Parse("ORDER BY");
        act.Should().Throw<QueryException>();
    }

    [Fact]
    public void Order_with_trailing_comma_throws()
    {
        var act = () => QueryParser.Parse("ORDER BY Cost,");
        act.Should().Throw<QueryException>();
    }

    [Fact]
    public void Order_with_unknown_direction_throws()
    {
        var act = () => QueryParser.Parse("ORDER BY Cost SIDEWAYS");
        act.Should().Throw<QueryException>();
    }
}
