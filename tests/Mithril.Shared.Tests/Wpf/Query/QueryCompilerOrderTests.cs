using System;
using System.Collections.Generic;
using System.ComponentModel;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

public class QueryCompilerOrderTests
{
    private sealed record Row(string Name, int Cost, DateTime Updated);

    private static IReadOnlyDictionary<string, ColumnBinding> Schema =>
        ColumnBindingHelper.BuildFromProperties(typeof(Row));

    [Fact]
    public void Empty_order_compiles_to_empty_descriptors()
    {
        var result = QueryCompiler.CompileOrder(Array.Empty<OrderSpec>(), Schema);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Single_key_ascending_compiles()
    {
        var result = QueryCompiler.CompileOrder(
            new[] { new OrderSpec("Cost", OrderDirection.Ascending) }, Schema);
        result.Should().HaveCount(1);
        result[0].PropertyName.Should().Be("Cost");
        result[0].Direction.Should().Be(ListSortDirection.Ascending);
    }

    [Fact]
    public void Direction_descending_maps_through()
    {
        var result = QueryCompiler.CompileOrder(
            new[] { new OrderSpec("Cost", OrderDirection.Descending) }, Schema);
        result[0].Direction.Should().Be(ListSortDirection.Descending);
    }

    [Fact]
    public void Multi_key_preserves_order()
    {
        var result = QueryCompiler.CompileOrder(
            new[]
            {
                new OrderSpec("Cost", OrderDirection.Descending),
                new OrderSpec("Name", OrderDirection.Ascending),
            }, Schema);
        result.Should().HaveCount(2);
        result[0].PropertyName.Should().Be("Cost");
        result[1].PropertyName.Should().Be("Name");
    }

    [Fact]
    public void Unknown_column_throws()
    {
        var act = () => QueryCompiler.CompileOrder(
            new[] { new OrderSpec("DoesNotExist", OrderDirection.Ascending) }, Schema);
        act.Should().Throw<QueryException>()
            .WithMessage("*Unknown column 'DoesNotExist'*");
    }

    [Fact]
    public void Column_name_resolves_case_insensitively_by_default()
    {
        var result = QueryCompiler.CompileOrder(
            new[] { new OrderSpec("cost", OrderDirection.Ascending) }, Schema);
        result[0].PropertyName.Should().Be("Cost");
    }

    [Fact]
    public void Column_name_is_case_sensitive_when_requested()
    {
        var act = () => QueryCompiler.CompileOrder(
            new[] { new OrderSpec("cost", OrderDirection.Ascending) }, Schema, caseSensitive: true);
        act.Should().Throw<QueryException>();
    }

    [Fact]
    public void Case_sensitive_matching_case_still_compiles()
    {
        var result = QueryCompiler.CompileOrder(
            new[] { new OrderSpec("Cost", OrderDirection.Ascending) }, Schema, caseSensitive: true);
        result.Should().HaveCount(1);
        result[0].PropertyName.Should().Be("Cost");
    }

    private sealed record NonComparableRow(object Blob);

    [Fact]
    public void Non_comparable_column_throws()
    {
        var schema = ColumnBindingHelper.BuildFromProperties(typeof(NonComparableRow));
        var act = () => QueryCompiler.CompileOrder(
            new[] { new OrderSpec("Blob", OrderDirection.Ascending) }, schema);
        act.Should().Throw<QueryException>()
            .WithMessage("*not sortable*");
    }

    private sealed record NullableRow(int? Maybe);

    [Fact]
    public void Nullable_value_type_is_sortable()
    {
        var schema = ColumnBindingHelper.BuildFromProperties(typeof(NullableRow));
        var result = QueryCompiler.CompileOrder(
            new[] { new OrderSpec("Maybe", OrderDirection.Ascending) }, schema);
        result.Should().HaveCount(1);
        result[0].PropertyName.Should().Be("Maybe");
    }
}
