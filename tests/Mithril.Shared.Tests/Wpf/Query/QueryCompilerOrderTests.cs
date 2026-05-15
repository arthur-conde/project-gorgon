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

    // ─────────────────────────────────────────────────────────────────────
    // CompileOrderComparer — produces the IComparer that ListCollectionView.CustomSort
    // (and QueryableSource.ApplyOrdered) consume to apply natural sort to string keys.

    private static readonly Row[] BiteDataset =
    {
        new("Bite",     2, default),
        new("Bite 11", 125, default),
        new("Bite 2",  10, default),
        new("Bite 10", 116, default),
    };

    [Fact]
    public void CompileOrderComparer_string_key_natural_sorts()
    {
        var cmp = QueryCompiler.CompileOrderComparer(
            new[] { new OrderSpec("Name", OrderDirection.Ascending) }, Schema);
        var sorted = BiteDataset.OrderBy(r => (object)r, cmp).Select(r => r.Name).ToArray();
        sorted.Should().Equal("Bite", "Bite 2", "Bite 10", "Bite 11");
    }

    [Fact]
    public void CompileOrderComparer_descending_reverses_natural_order()
    {
        var cmp = QueryCompiler.CompileOrderComparer(
            new[] { new OrderSpec("Name", OrderDirection.Descending) }, Schema);
        var sorted = BiteDataset.OrderBy(r => (object)r, cmp).Select(r => r.Name).ToArray();
        sorted.Should().Equal("Bite 11", "Bite 10", "Bite 2", "Bite");
    }

    [Fact]
    public void CompileOrderComparer_numeric_key_still_compares_numerically()
    {
        // int columns should sort by magnitude, not lex (the natural-sort comparer is
        // string-only; numerics route through Comparer<object>.Default).
        var cmp = QueryCompiler.CompileOrderComparer(
            new[] { new OrderSpec("Cost", OrderDirection.Ascending) }, Schema);
        var sorted = BiteDataset.OrderBy(r => (object)r, cmp).Select(r => r.Cost).ToArray();
        sorted.Should().Equal(2, 10, 116, 125);
    }

    [Fact]
    public void CompileOrderComparer_multi_key_primary_string_secondary_numeric()
    {
        // Same primary name → tie-break by Cost DESC.
        var dataset = new[]
        {
            new Row("Claw", 10, default),
            new Row("Claw", 50, default),
            new Row("Claw", 30, default),
            new Row("Bite", 7,  default),
        };
        var cmp = QueryCompiler.CompileOrderComparer(
            new[]
            {
                new OrderSpec("Name", OrderDirection.Ascending),
                new OrderSpec("Cost", OrderDirection.Descending),
            }, Schema);
        var sorted = dataset.OrderBy(r => (object)r, cmp).ToArray();
        sorted.Select(r => (r.Name, r.Cost)).Should().Equal(
            ("Bite", 7), ("Claw", 50), ("Claw", 30), ("Claw", 10));
    }

    [Fact]
    public void CompileOrderComparer_empty_order_returns_no_op()
    {
        // No keys → comparer says "equal" for everything; OrderBy is therefore stable
        // and returns the source order.
        var cmp = QueryCompiler.CompileOrderComparer(Array.Empty<OrderSpec>(), Schema);
        cmp.Compare(BiteDataset[0], BiteDataset[1]).Should().Be(0);
    }

    [Fact]
    public void CompileOrderComparer_unknown_column_throws()
    {
        var act = () => QueryCompiler.CompileOrderComparer(
            new[] { new OrderSpec("DoesNotExist", OrderDirection.Ascending) }, Schema);
        act.Should().Throw<QueryException>()
            .WithMessage("*Unknown column 'DoesNotExist'*");
    }

    [Fact]
    public void CompileOrderComparer_non_comparable_column_throws()
    {
        var schema = ColumnBindingHelper.BuildFromProperties(typeof(NonComparableRow));
        var act = () => QueryCompiler.CompileOrderComparer(
            new[] { new OrderSpec("Blob", OrderDirection.Ascending) }, schema);
        act.Should().Throw<QueryException>()
            .WithMessage("*not sortable*");
    }

    [Fact]
    public void CompileOrderComparer_handles_null_values_safely()
    {
        var rows = new[]
        {
            new Row(null!, 5, default),  // Name = null
            new Row("Apple", 1, default),
            new Row("Bite", 2, default),
        };
        var cmp = QueryCompiler.CompileOrderComparer(
            new[] { new OrderSpec("Name", OrderDirection.Ascending) }, Schema);
        var sorted = rows.OrderBy(r => (object)r, cmp).Select(r => r.Name).ToArray();
        sorted.Should().Equal(null, "Apple", "Bite");
    }
}
