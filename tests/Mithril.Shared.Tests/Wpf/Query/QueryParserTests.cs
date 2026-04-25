using System;
using FluentAssertions;
using Mithril.Shared.Wpf.Query;
using Xunit;

namespace Mithril.Shared.Tests.Wpf.Query;

public class QueryParserTests
{
    [Fact]
    public void Empty_query_returns_null()
    {
        QueryParser.Parse("").Should().BeNull();
        QueryParser.Parse("   ").Should().BeNull();
        QueryParser.Parse(null!).Should().BeNull();
    }

    [Fact]
    public void Simple_equality_comparison()
    {
        var ast = QueryParser.Parse("crop = 'red'");
        ast.Should().BeOfType<ComparisonNode>();
        var c = (ComparisonNode)ast!;
        c.Column.Should().Be("crop");
        c.Op.Should().Be(ComparisonOp.Eq);
        c.Value.Should().BeOfType<StringValue>().Which.Text.Should().Be("red");
    }

    [Fact]
    public void Numeric_operators_parse_correctly()
    {
        foreach (var (text, op) in new[]
        {
            ("samples = 5", ComparisonOp.Eq),
            ("samples != 5", ComparisonOp.Neq),
            ("samples <> 5", ComparisonOp.Neq),
            ("samples < 5", ComparisonOp.Lt),
            ("samples <= 5", ComparisonOp.Lte),
            ("samples > 5", ComparisonOp.Gt),
            ("samples >= 5", ComparisonOp.Gte),
        })
        {
            var ast = QueryParser.Parse(text);
            var c = ast.Should().BeOfType<ComparisonNode>().Subject;
            c.Op.Should().Be(op, $"operator in '{text}'");
            c.Value.Should().BeOfType<NumberValue>().Which.Value.Should().Be(5d);
        }
    }

    [Fact]
    public void Duration_literal_parses_into_timespan()
    {
        var ast = QueryParser.Parse("avg > 1m30s");
        var c = ast.Should().BeOfType<ComparisonNode>().Subject;
        c.Value.Should().BeOfType<DurationValue>().Which.Value.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void Duration_single_unit()
    {
        QueryParser.Parse("avg = 30s").Should().BeOfType<ComparisonNode>()
            .Which.Value.Should().BeOfType<DurationValue>()
            .Which.Value.Should().Be(TimeSpan.FromSeconds(30));

        QueryParser.Parse("dur = 2h").Should().BeOfType<ComparisonNode>()
            .Which.Value.Should().BeOfType<DurationValue>()
            .Which.Value.Should().Be(TimeSpan.FromHours(2));

        QueryParser.Parse("dur = 150ms").Should().BeOfType<ComparisonNode>()
            .Which.Value.Should().BeOfType<DurationValue>()
            .Which.Value.Should().Be(TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public void And_left_associative_with_higher_precedence_than_or()
    {
        // a OR b AND c  →  a OR (b AND c)
        var ast = QueryParser.Parse("a = 1 OR b = 2 AND c = 3");
        var or = ast.Should().BeOfType<OrNode>().Subject;
        or.Left.Should().BeOfType<ComparisonNode>().Which.Column.Should().Be("a");
        or.Right.Should().BeOfType<AndNode>();
    }

    [Fact]
    public void Parens_override_precedence()
    {
        // (a OR b) AND c
        var ast = QueryParser.Parse("(a = 1 OR b = 2) AND c = 3");
        var and = ast.Should().BeOfType<AndNode>().Subject;
        and.Left.Should().BeOfType<OrNode>();
        and.Right.Should().BeOfType<ComparisonNode>().Which.Column.Should().Be("c");
    }

    [Fact]
    public void Unary_not_wraps_predicate()
    {
        var ast = QueryParser.Parse("NOT a = 1");
        ast.Should().BeOfType<NotNode>()
            .Which.Inner.Should().BeOfType<ComparisonNode>();
    }

    [Fact]
    public void Like_and_not_like()
    {
        var likeAst = QueryParser.Parse("crop LIKE '%aster%'");
        likeAst.Should().BeOfType<LikeNode>()
            .Which.Negated.Should().BeFalse();
        ((LikeNode)likeAst!).Pattern.Should().Be("%aster%");

        var notLikeAst = QueryParser.Parse("crop NOT LIKE '%aster%'");
        notLikeAst.Should().BeOfType<LikeNode>()
            .Which.Negated.Should().BeTrue();
    }

    [Fact]
    public void In_list_parses_multiple_values()
    {
        var ast = QueryParser.Parse("char IN ('Emraell', 'Bob', 'Cindy')");
        var inNode = ast.Should().BeOfType<InNode>().Subject;
        inNode.Negated.Should().BeFalse();
        inNode.Values.Should().HaveCount(3);
        inNode.Values[0].Should().BeOfType<StringValue>().Which.Text.Should().Be("Emraell");
    }

    [Fact]
    public void Not_in_list()
    {
        var ast = QueryParser.Parse("char NOT IN ('Emraell')");
        ast.Should().BeOfType<InNode>().Which.Negated.Should().BeTrue();
    }

    [Fact]
    public void Between_requires_low_and_high()
    {
        var ast = QueryParser.Parse("avg BETWEEN 30s AND 2m");
        var b = ast.Should().BeOfType<BetweenNode>().Subject;
        b.Low.Should().BeOfType<DurationValue>();
        b.High.Should().BeOfType<DurationValue>();
    }

    [Fact]
    public void Is_null_and_is_not_null()
    {
        QueryParser.Parse("config IS NULL").Should().BeOfType<IsNullNode>()
            .Which.Negated.Should().BeFalse();
        QueryParser.Parse("config IS NOT NULL").Should().BeOfType<IsNullNode>()
            .Which.Negated.Should().BeTrue();
    }

    [Fact]
    public void Quoted_strings_support_both_quote_styles_and_escaped_quotes()
    {
        QueryParser.Parse("crop = 'Red ''Aster'''").Should().BeOfType<ComparisonNode>()
            .Which.Value.Should().BeOfType<StringValue>()
            .Which.Text.Should().Be("Red 'Aster'");

        QueryParser.Parse("crop = \"Red\"").Should().BeOfType<ComparisonNode>()
            .Which.Value.Should().BeOfType<StringValue>()
            .Which.Text.Should().Be("Red");
    }

    [Fact]
    public void Keywords_are_case_insensitive()
    {
        var ast = QueryParser.Parse("a = 1 and B like '%x%' or not c in (1)");
        ast.Should().BeOfType<OrNode>();
    }

    [Fact]
    public void Boolean_and_null_literals()
    {
        QueryParser.Parse("flag = TRUE").Should().BeOfType<ComparisonNode>()
            .Which.Value.Should().BeOfType<BoolValue>().Which.Value.Should().BeTrue();
        QueryParser.Parse("flag = FALSE").Should().BeOfType<ComparisonNode>()
            .Which.Value.Should().BeOfType<BoolValue>().Which.Value.Should().BeFalse();
        QueryParser.Parse("flag = NULL").Should().BeOfType<ComparisonNode>()
            .Which.Value.Should().BeOfType<NullValue>();
    }

    [Fact]
    public void Error_on_unterminated_string_reports_position()
    {
        var ex = Record.Exception(() => QueryParser.Parse("crop = 'red")) as QueryException;
        ex.Should().NotBeNull();
        ex!.Position.Should().Be(7);
    }

    [Fact]
    public void Error_on_missing_rhs()
    {
        var ex = Record.Exception(() => QueryParser.Parse("crop =")) as QueryException;
        ex.Should().NotBeNull();
    }

    [Fact]
    public void Error_on_trailing_tokens()
    {
        var ex = Record.Exception(() => QueryParser.Parse("crop = 'red' xxx")) as QueryException;
        ex.Should().NotBeNull();
    }

    [Fact]
    public void Negative_numbers()
    {
        QueryParser.Parse("delta > -5").Should().BeOfType<ComparisonNode>()
            .Which.Value.Should().BeOfType<NumberValue>().Which.Value.Should().Be(-5d);
    }

    [Fact]
    public void Decimal_number()
    {
        QueryParser.Parse("ratio >= 0.75").Should().BeOfType<ComparisonNode>()
            .Which.Value.Should().BeOfType<NumberValue>().Which.Value.Should().Be(0.75);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("red aster", false)]
    [InlineData("not a thing", false)]   // lowercase not -> bare text
    [InlineData("is it ripe", false)]    // lowercase is -> bare text
    [InlineData("or whatever", false)]   // lowercase or -> bare text
    [InlineData("NOT ripe", true)]       // uppercase NOT -> grammar
    [InlineData("crop = 'red'", true)]   // = is grammar
    [InlineData("samples > 5", true)]
    [InlineData("crop LIKE '%x%'", true)]
    [InlineData("(a OR b)", true)]
    [InlineData("x IS NULL", true)]
    [InlineData("char IN (1,2)", true)]
    [InlineData("apostrophe's", true)]   // quote char -> grammar (it's a string start)
    [InlineData("Timestamp BEFORE NOW()", true)]
    public void LooksLikeGrammar_classifies_correctly(string input, bool expected)
    {
        QueryParser.LooksLikeGrammar(input).Should().Be(expected);
    }

    [Fact]
    public void LooksLikeGrammar_with_known_columns_treats_column_tokens_as_grammar_intent()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CropType", "CharName" };
        // Without known columns, this is bare text (no operators, no uppercase keywords).
        QueryParser.LooksLikeGrammar("CropType LIK").Should().BeFalse();
        // With the column set, the bare mention of CropType flips it to grammar-intent.
        QueryParser.LooksLikeGrammar("CropType LIK", known).Should().BeTrue();
        // Case-insensitive column match.
        QueryParser.LooksLikeGrammar("croptype lik", known).Should().BeTrue();
        // Random word that isn't a column stays bare.
        QueryParser.LooksLikeGrammar("red aster", known).Should().BeFalse();
    }

    [Fact]
    public void Before_and_After_parse_as_comparison_operators()
    {
        var ast = QueryParser.Parse("Timestamp BEFORE '2026-04-22'");
        ast.Should().BeOfType<ComparisonNode>().Which.Op.Should().Be(ComparisonOp.Lt);

        var ast2 = QueryParser.Parse("Timestamp AFTER '2026-04-22'");
        ast2.Should().BeOfType<ComparisonNode>().Which.Op.Should().Be(ComparisonOp.Gt);
    }

    [Fact]
    public void Now_function_parses_to_DateTimeValue()
    {
        var before = DateTime.Now;
        var ast = QueryParser.Parse("Timestamp BEFORE NOW()");
        var after = DateTime.Now;
        var c = ast.Should().BeOfType<ComparisonNode>().Subject;
        var dt = c.Value.Should().BeOfType<DateTimeValue>().Subject;
        dt.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Today_function_parses_to_midnight()
    {
        var ast = QueryParser.Parse("Timestamp AFTER TODAY()");
        var c = ast.Should().BeOfType<ComparisonNode>().Subject;
        c.Value.Should().BeOfType<DateTimeValue>().Which.Value.Should().Be(DateTime.Today);
    }

    [Fact]
    public void Unknown_function_throws_with_suggestion()
    {
        var ex = Record.Exception(() => QueryParser.Parse("x = FOO()")) as QueryException;
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("FOO").And.Contain("NOW").And.Contain("TODAY");
    }

    [Fact]
    public void Contains_parses_to_StringMatchNode()
    {
        var ast = QueryParser.Parse("crop CONTAINS 'aster'");
        var n = ast.Should().BeOfType<StringMatchNode>().Subject;
        n.Column.Should().Be("crop");
        n.Text.Should().Be("aster");
        n.Kind.Should().Be(StringMatchKind.Contains);
        n.Negated.Should().BeFalse();
    }

    [Fact]
    public void Not_Contains_is_negated()
    {
        var ast = QueryParser.Parse("crop NOT CONTAINS 'aster'");
        ast.Should().BeOfType<StringMatchNode>().Which.Negated.Should().BeTrue();
    }

    [Fact]
    public void Startswith_and_Endswith_parse()
    {
        QueryParser.Parse("crop STARTSWITH 'Red'")
            .Should().BeOfType<StringMatchNode>()
            .Which.Kind.Should().Be(StringMatchKind.StartsWith);
        QueryParser.Parse("crop ENDSWITH 'Rye'")
            .Should().BeOfType<StringMatchNode>()
            .Which.Kind.Should().Be(StringMatchKind.EndsWith);
    }

    [Fact]
    public void String_match_keywords_are_case_insensitive()
    {
        QueryParser.Parse("crop contains 'x'").Should().BeOfType<StringMatchNode>();
        QueryParser.Parse("crop StartsWith 'x'").Should().BeOfType<StringMatchNode>();
    }
}
