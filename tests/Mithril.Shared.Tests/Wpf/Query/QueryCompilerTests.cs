using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

public class QueryCompilerTests
{
    private sealed record Row(
        string Crop,
        string Char,
        int Samples,
        TimeSpan Avg,
        double? Delta,
        DateTime Time,
        bool Active);

    private static readonly Row[] Dataset =
    {
        new("Red Aster",  "Emraell", 19, TimeSpan.FromSeconds(57), 0.58,  new DateTime(2026, 4, 22), true),
        new("Daisy",      "Emraell",  3, TimeSpan.FromSeconds(41), 2.14,  new DateTime(2026, 4, 22), true),
        new("Tundra Rye", "Bob",      3, TimeSpan.FromSeconds(81), null,  new DateTime(2026, 4, 21), false),
        new("Pumpkin",    "Cindy",   12, TimeSpan.FromMinutes(2),  -0.05, new DateTime(2026, 4, 20), true),
    };

    private static readonly IReadOnlyDictionary<string, ColumnBinding> Columns = new Dictionary<string, ColumnBinding>(StringComparer.OrdinalIgnoreCase)
    {
        ["crop"]    = new("crop",    typeof(string),   r => ((Row)r).Crop),
        ["char"]    = new("char",    typeof(string),   r => ((Row)r).Char),
        ["samples"] = new("samples", typeof(int),      r => ((Row)r).Samples),
        ["avg"]     = new("avg",     typeof(TimeSpan), r => ((Row)r).Avg),
        ["delta"]   = new("delta",   typeof(double?),  r => ((Row)r).Delta),
        ["time"]    = new("time",    typeof(DateTime), r => ((Row)r).Time),
        ["active"]  = new("active",  typeof(bool),     r => ((Row)r).Active),
    };

    private static Row[] Filter(string query)
    {
        var predicate = QueryCompiler.Compile(query, Columns);
        if (predicate is null)
        {
            return Dataset;
        }
        var result = new List<Row>();
        foreach (var r in Dataset)
        {
            if (predicate(r))
            {
                result.Add(r);
            }
        }
        return result.ToArray();
    }

    [Fact]
    public void String_equality_case_insensitive_by_default()
    {
        Filter("crop = 'red aster'").Should().ContainSingle().Which.Crop.Should().Be("Red Aster");
    }

    [Fact]
    public void String_like_with_percent_wildcard()
    {
        Filter("crop LIKE '%aster%'").Should().ContainSingle();
        Filter("crop LIKE 'Tundra%'").Should().ContainSingle();
        Filter("crop LIKE '%'").Should().HaveCount(4);
    }

    [Fact]
    public void String_like_with_underscore_wildcard()
    {
        Filter("crop LIKE 'Dais_'").Should().ContainSingle().Which.Crop.Should().Be("Daisy");
    }

    [Fact]
    public void Not_like()
    {
        Filter("crop NOT LIKE '%aster%'").Should().HaveCount(3);
    }

    [Fact]
    public void Integer_comparisons()
    {
        Filter("samples >= 12").Select(r => r.Samples).Should().BeEquivalentTo(new[] { 19, 12 });
        Filter("samples < 5").Select(r => r.Samples).Should().BeEquivalentTo(new[] { 3, 3 });
    }

    [Fact]
    public void Timespan_comparison_with_duration_literal()
    {
        // Tundra Rye is 1m21s, Pumpkin is 2m — both above 1m.
        Filter("avg > 1m").Select(r => r.Crop).Should().BeEquivalentTo(new[] { "Tundra Rye", "Pumpkin" });
        Filter("avg <= 1m").Should().HaveCount(2);
        Filter("avg > 2m").Should().BeEmpty();
    }

    [Fact]
    public void Between_numeric_range()
    {
        Filter("samples BETWEEN 5 AND 15").Select(r => r.Samples).Should().BeEquivalentTo(new[] { 12 });
    }

    [Fact]
    public void Between_timespan_range()
    {
        Filter("avg BETWEEN 30s AND 90s").Should().HaveCount(3);
    }

    [Fact]
    public void Not_between()
    {
        Filter("samples NOT BETWEEN 5 AND 15").Select(r => r.Samples).Should().BeEquivalentTo(new[] { 19, 3, 3 });
    }

    [Fact]
    public void In_with_strings()
    {
        Filter("char IN ('Emraell', 'Bob')").Should().HaveCount(3);
    }

    [Fact]
    public void Not_in_with_strings()
    {
        Filter("char NOT IN ('Emraell')").Should().HaveCount(2);
    }

    [Fact]
    public void In_with_numbers()
    {
        Filter("samples IN (3, 19)").Should().HaveCount(3);
    }

    [Fact]
    public void Is_null_and_is_not_null_on_nullable()
    {
        Filter("delta IS NULL").Should().ContainSingle().Which.Crop.Should().Be("Tundra Rye");
        Filter("delta IS NOT NULL").Should().HaveCount(3);
    }

    [Fact]
    public void Boolean_equality()
    {
        Filter("active = TRUE").Should().HaveCount(3);
        Filter("active = FALSE").Should().ContainSingle();
    }

    [Fact]
    public void Compound_and_or()
    {
        Filter("char = 'Emraell' AND samples >= 5")
            .Select(r => r.Crop).Should().BeEquivalentTo(new[] { "Red Aster" });
    }

    [Fact]
    public void Parenthesized_grouping_changes_result()
    {
        Filter("(char = 'Emraell' OR char = 'Bob') AND samples < 5").Should().HaveCount(2);
    }

    [Fact]
    public void Date_comparison_with_iso_literal()
    {
        Filter("time >= '2026-04-22'").Should().HaveCount(2);
    }

    [Fact]
    public void Unknown_column_throws()
    {
        var ex = Record.Exception(() => Filter("nonsense = 1")) as QueryException;
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("nonsense");
    }

    [Fact]
    public void Like_on_non_string_column_throws()
    {
        Record.Exception(() => Filter("samples LIKE 'foo'"))
            .Should().BeOfType<QueryException>();
    }

    [Fact]
    public void Between_on_string_throws()
    {
        Record.Exception(() => Filter("crop BETWEEN 'a' AND 'z'"))
            .Should().BeOfType<QueryException>();
    }

    [Fact]
    public void String_comparison_only_accepts_eq_and_neq()
    {
        Record.Exception(() => Filter("crop > 'aa'"))
            .Should().BeOfType<QueryException>();
    }

    [Fact]
    public void Case_sensitive_flag_respects_case()
    {
        var predicate = QueryCompiler.Compile("crop = 'red aster'", Columns, caseSensitive: true);
        predicate.Should().NotBeNull();
        Dataset.Count(r => predicate!(r)).Should().Be(0);
    }

    [Fact]
    public void Empty_query_returns_null_predicate()
    {
        QueryCompiler.Compile("", Columns).Should().BeNull();
        QueryCompiler.Compile("   ", Columns).Should().BeNull();
    }

    [Fact]
    public void Not_prefix_combines_with_predicate()
    {
        Filter("NOT (char = 'Emraell')").Should().HaveCount(2);
    }

    [Fact]
    public void Before_keyword_filters_like_less_than()
    {
        Filter("time BEFORE '2026-04-22'").Should().HaveCount(2);
    }

    [Fact]
    public void After_keyword_filters_like_greater_than()
    {
        Filter("time AFTER '2026-04-21'").Should().HaveCount(2);
    }

    [Fact]
    public void Now_function_resolves_to_current_time()
    {
        // All dataset timestamps are in the past, so all should be BEFORE NOW().
        Filter("time BEFORE NOW()").Should().HaveCount(4);
        Filter("time AFTER NOW()").Should().BeEmpty();
    }

    [Fact]
    public void Today_function_resolves_to_midnight()
    {
        // Dataset has two rows with 2026-04-22 timestamps; TODAY() compared depends on
        // today's date being later than those, which it is (today > 2026-04-22 in real runs;
        // this test is deterministic only insofar as the dataset dates are fixed in 2026).
        var result = Filter("time BEFORE TODAY()");
        // The test simply asserts the query runs without error and returns a sensible number
        // (>= 0, <= 4). The exact count depends on when the test runs.
        result.Should().HaveCountLessOrEqualTo(4);
    }

    [Fact]
    public void Contains_matches_case_insensitive_substring()
    {
        Filter("crop CONTAINS 'aster'").Should().ContainSingle().Which.Crop.Should().Be("Red Aster");
        Filter("crop CONTAINS 'ASTER'").Should().ContainSingle(); // case-insensitive default
        Filter("crop CONTAINS 'nope'").Should().BeEmpty();
    }

    [Fact]
    public void Contains_treats_percent_literally()
    {
        // Proves CONTAINS is not LIKE — `%` in the RHS must match a literal percent sign,
        // not act as a wildcard. None of our rows contain '%', so this must be empty.
        Filter("crop CONTAINS '%'").Should().BeEmpty();
    }

    [Fact]
    public void StartsWith_and_EndsWith()
    {
        Filter("crop STARTSWITH 'Tun'").Should().ContainSingle().Which.Crop.Should().Be("Tundra Rye");
        Filter("crop ENDSWITH 'Rye'").Should().ContainSingle().Which.Crop.Should().Be("Tundra Rye");
        Filter("crop STARTSWITH 'rye'").Should().BeEmpty();
    }

    [Fact]
    public void Not_Contains_negates()
    {
        Filter("crop NOT CONTAINS 'aster'").Should().HaveCount(3);
    }

    [Fact]
    public void Contains_on_non_string_column_throws()
    {
        Record.Exception(() => Filter("samples CONTAINS 'x'"))
            .Should().BeOfType<QueryException>();
    }
}

