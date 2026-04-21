using FluentAssertions;
using Pippin.Parsing;
using Xunit;

namespace Pippin.Tests;

public class GourmandLogParserTests
{
    private readonly GourmandLogParser _parser = new();
    private readonly DateTime _ts = new(2026, 4, 19, 22, 4, 9);

    private const string FullReportLine =
        """[22:04:09] LocalPlayer: ProcessBook("Skill Info", "Foods Consumed:\n\n  8-Year Steamed Cake (HAS DAIRY): 1\n  Apple Juice: 8\n  Bacon (HAS MEAT): 2\n  Fish and Mushroom Scramble (HAS EGGS): 1\n  Pork Chops with Watercress (HAS MEAT): 2\n", "SkillReport", "", "", False, False, False, False, False, "")""";

    [Fact]
    public void Parses_full_report_with_multiple_foods()
    {
        var result = _parser.TryParse(FullReportLine, _ts);

        result.Should().BeOfType<FoodsConsumedReport>();
        var report = (FoodsConsumedReport)result!;
        report.Foods.Should().HaveCount(5);
    }

    [Fact]
    public void Parses_food_name_with_hyphen_and_number()
    {
        var result = _parser.TryParse(FullReportLine, _ts) as FoodsConsumedReport;

        var cake = result!.Foods.First(f => f.Name == "8-Year Steamed Cake");
        cake.Count.Should().Be(1);
        cake.Tags.Should().ContainSingle().Which.Should().Be("DAIRY");
    }

    [Fact]
    public void Parses_food_with_no_tags()
    {
        var result = _parser.TryParse(FullReportLine, _ts) as FoodsConsumedReport;

        var juice = result!.Foods.First(f => f.Name == "Apple Juice");
        juice.Count.Should().Be(8);
        juice.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Parses_food_with_meat_tag()
    {
        var result = _parser.TryParse(FullReportLine, _ts) as FoodsConsumedReport;

        var bacon = result!.Foods.First(f => f.Name == "Bacon");
        bacon.Count.Should().Be(2);
        bacon.Tags.Should().ContainSingle().Which.Should().Be("MEAT");
    }

    [Fact]
    public void Parses_food_with_eggs_tag()
    {
        var result = _parser.TryParse(FullReportLine, _ts) as FoodsConsumedReport;

        var scramble = result!.Foods.First(f => f.Name == "Fish and Mushroom Scramble");
        scramble.Count.Should().Be(1);
        scramble.Tags.Should().ContainSingle().Which.Should().Be("EGGS");
    }

    [Fact]
    public void Returns_null_for_non_matching_log_line()
    {
        var line = "[22:04:09] LocalPlayer: ProcessAddPlayer(123, \"Emraell\")";
        _parser.TryParse(line, _ts).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_other_ProcessBook_report()
    {
        var line = """[22:04:09] LocalPlayer: ProcessBook("Skill Info", "Some other report", "SkillReport", "", "", False, False, False, False, False, "")""";
        _parser.TryParse(line, _ts).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_unrelated_line()
    {
        var line = "This is just chat text";
        _parser.TryParse(line, _ts).Should().BeNull();
    }
}
