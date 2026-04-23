using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Gorgon.Shared.Wpf.Query;
using Xunit;

namespace Gorgon.Shared.Tests.Wpf.Query;

public class QueryCompletionProviderTests
{
    private static readonly IReadOnlyList<ColumnSchema> Columns = new[]
    {
        new ColumnSchema("crop", typeof(string), IsNullable: false),
        new ColumnSchema("char", typeof(string), IsNullable: false),
        new ColumnSchema("samples", typeof(int), IsNullable: false),
        new ColumnSchema("avg", typeof(TimeSpan), IsNullable: false),
        new ColumnSchema("delta", typeof(double?), IsNullable: true),
        new ColumnSchema("active", typeof(bool), IsNullable: false),
    };

    private static IReadOnlyList<CompletionItem> Suggest(string query, int caret, Func<string, IReadOnlyList<string>>? sampler = null)
        => QueryCompletionProvider.Suggest(query, caret, Columns, sampler);

    [Fact]
    public void Empty_query_suggests_columns()
    {
        var results = Suggest("", 0);
        results.Select(r => r.Label).Should().Contain(new[] { "crop", "samples", "active" });
    }

    [Fact]
    public void Partial_column_prefix_filters_suggestions()
    {
        var results = Suggest("cr", 2);
        results.Should().OnlyContain(r => r.Label.StartsWith("cr", StringComparison.OrdinalIgnoreCase));
        results.Select(r => r.Label).Should().Contain("crop");
    }

    [Fact]
    public void After_column_suggests_operators_for_strings()
    {
        var results = Suggest("crop ", 5);
        var labels = results.Select(r => r.Label).ToList();
        labels.Should().Contain(new[] { "CONTAINS", "NOT CONTAINS", "STARTSWITH", "ENDSWITH", "=", "!=", "LIKE", "NOT LIKE", "IN", "NOT IN" });
        labels.Should().NotContain(new[] { "<", ">", "BETWEEN" });
        // CONTAINS should be the first string suggestion (friendliest form).
        labels.First().Should().Be("CONTAINS");
    }

    [Fact]
    public void After_Contains_suggests_sampled_string_values()
    {
        IReadOnlyList<string> sampler(string col) => col == "crop" ? new[] { "Red Aster", "Daisy" } : Array.Empty<string>();
        var results = Suggest("crop CONTAINS ", 14, sampler);
        results.Select(r => r.Label).Should().Contain(new[] { "'Red Aster'", "'Daisy'" });
    }

    [Fact]
    public void After_column_suggests_full_range_for_numerics()
    {
        var results = Suggest("samples ", 8);
        var labels = results.Select(r => r.Label).ToList();
        labels.Should().Contain(new[] { "<", "<=", ">", ">=", "BETWEEN", "IN" });
    }

    [Fact]
    public void Nullable_column_adds_is_null_suggestions()
    {
        var results = Suggest("delta ", 6);
        var labels = results.Select(r => r.Label).ToList();
        labels.Should().Contain(new[] { "IS NULL", "IS NOT NULL" });
    }

    [Fact]
    public void Boolean_column_only_offers_equality_operators()
    {
        var results = Suggest("active ", 7);
        var labels = results.Select(r => r.Label).ToList();
        labels.Should().Contain(new[] { "=", "!=" });
        labels.Should().NotContain(new[] { "<", "LIKE", "BETWEEN" });
    }

    [Fact]
    public void After_equals_on_bool_suggests_TRUE_FALSE()
    {
        var results = Suggest("active = ", 9);
        results.Select(r => r.Label).Should().Contain(new[] { "TRUE", "FALSE" });
    }

    [Fact]
    public void After_equals_on_string_suggests_sampled_values()
    {
        IReadOnlyList<string> sampler(string col) => col == "crop" ? new[] { "Red Aster", "Daisy" } : Array.Empty<string>();
        var results = Suggest("crop = ", 7, sampler);
        results.Select(r => r.Label).Should().Contain(new[] { "'Red Aster'", "'Daisy'" });
    }

    [Fact]
    public void After_value_suggests_combinators()
    {
        var results = Suggest("samples = 5 ", 12);
        results.Select(r => r.Label).Should().Contain(new[] { "AND", "OR" });
    }

    [Fact]
    public void Between_low_value_suggests_AND_next()
    {
        var results = Suggest("avg BETWEEN 30s ", 16);
        results.Select(r => r.Label).Should().Contain("AND");
        results.Select(r => r.Label).Should().NotContain("OR");
    }

    [Fact]
    public void After_AND_suggests_columns_again()
    {
        var results = Suggest("samples = 5 AND ", 16);
        results.Select(r => r.Label).Should().Contain(new[] { "crop", "char", "samples" });
    }

    [Fact]
    public void Partial_keyword_prefix_filters()
    {
        var results = Suggest("crop LIK", 8);
        results.Select(r => r.Label).Should().Contain("LIKE");
        results.Select(r => r.Label).Should().NotContain("IN");
    }

    [Fact]
    public void Unknown_token_does_not_crash()
    {
        // Should not throw even with garbage in the query.
        var results = Suggest("@@@ >>> ", 8);
        results.Should().NotBeNull();
    }

    [Fact]
    public void Replace_span_covers_the_partial_identifier()
    {
        var results = Suggest("cr", 2);
        var crop = results.Single(r => r.Label == "crop");
        crop.ReplaceStart.Should().Be(0);
        crop.ReplaceEnd.Should().Be(2);
    }

    [Fact]
    public void Replace_span_at_caret_when_no_partial()
    {
        var results = Suggest("crop = ", 7);
        foreach (var r in results)
        {
            r.ReplaceStart.Should().Be(7);
            r.ReplaceEnd.Should().Be(7);
        }
    }

    [Fact]
    public void String_values_are_quoted_and_escape_single_quotes()
    {
        IReadOnlyList<string> sampler(string col) => new[] { "O'Malley" };
        var results = Suggest("crop = ", 7, sampler);
        results.Should().ContainSingle(r => r.Label == "'O''Malley'");
    }
}
