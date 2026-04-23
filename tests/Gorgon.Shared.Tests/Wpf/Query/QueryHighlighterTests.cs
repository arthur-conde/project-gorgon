using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Gorgon.Shared.Wpf.Query;
using Xunit;

namespace Gorgon.Shared.Tests.Wpf.Query;

public class QueryHighlighterTests
{
    private static readonly IReadOnlySet<string> Columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "crop", "char", "samples", "avg", "delta", "time",
    };

    private static HighlightKind At(string query, int offset)
    {
        var spans = QueryHighlighter.Highlight(query, Columns);
        return spans.First(s => s.Start <= offset && offset < s.Start + s.Length).Kind;
    }

    [Fact]
    public void Known_column_is_classified_as_Column()
    {
        At("crop = 'red'", 0).Should().Be(HighlightKind.Column);
    }

    [Fact]
    public void Unknown_column_is_classified_as_UnknownColumn()
    {
        At("nonsense = 1", 0).Should().Be(HighlightKind.UnknownColumn);
    }

    [Fact]
    public void Keyword_is_classified_as_Keyword()
    {
        At("crop = 'red' AND samples > 5", 13).Should().Be(HighlightKind.Keyword);
        At("crop LIKE '%red%'", 5).Should().Be(HighlightKind.Keyword);
    }

    [Fact]
    public void Operator_is_classified_as_Operator()
    {
        At("samples >= 5", 8).Should().Be(HighlightKind.Operator);
    }

    [Fact]
    public void String_includes_the_quotes_in_its_span()
    {
        var spans = QueryHighlighter.Highlight("crop = 'red'", Columns);
        var stringSpan = spans.Single(s => s.Kind == HighlightKind.String);
        stringSpan.Start.Should().Be(7);
        stringSpan.Length.Should().Be(5); // 'red' = 5 chars including quotes
    }

    [Fact]
    public void Number_and_duration_are_classified_separately()
    {
        At("samples = 5", 10).Should().Be(HighlightKind.Number);
        At("avg = 1m30s", 6).Should().Be(HighlightKind.Duration);
    }

    [Fact]
    public void Punctuation_is_classified_as_Punct()
    {
        At("char IN ('a','b')", 8).Should().Be(HighlightKind.Punct);
    }

    [Fact]
    public void Permissive_unterminated_string_highlights_to_end()
    {
        var spans = QueryHighlighter.Highlight("crop = 'red", Columns);
        var s = spans.Single(sp => sp.Kind == HighlightKind.String);
        s.Start.Should().Be(7);
        s.Length.Should().Be(4); // 'red — opening quote + 3 chars, no closing
    }

    [Fact]
    public void Permissive_unknown_character_produces_Error_span()
    {
        var spans = QueryHighlighter.Highlight("crop @ 1", Columns);
        spans.Should().Contain(s => s.Kind == HighlightKind.Error);
    }

    [Fact]
    public void Empty_query_returns_no_spans()
    {
        QueryHighlighter.Highlight("", Columns).Should().BeEmpty();
    }

    [Fact]
    public void Column_match_is_case_insensitive()
    {
        At("CROP = 1", 0).Should().Be(HighlightKind.Column);
        At("Crop = 1", 0).Should().Be(HighlightKind.Column);
    }
}
