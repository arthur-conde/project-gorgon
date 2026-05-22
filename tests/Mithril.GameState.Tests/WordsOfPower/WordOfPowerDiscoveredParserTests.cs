using FluentAssertions;
using Mithril.GameState.WordsOfPower.Parsing;
using Xunit;

namespace Mithril.GameState.Tests.WordsOfPower;

public sealed class WordOfPowerDiscoveredParserTests
{
    // Concrete lines captured from Player.log on 2026-04-22 — envelope-stripped
    // (the L0.5 router eats the `[ts] LocalPlayer:` prefix before the parser sees it).
    public static TheoryData<string, string, string, string> DiscoveryCases() => new()
    {
        {
            "ProcessBook(\"You discovered a word of power!\", \"You've discovered a word of power: <sel>ZOCKZECH</sel>Speak it out loud for this effect:\\n\\n<b><size=125%>Word of Power: Anemia</size></b>\\nSpeaking this word weakens your body so that all attacks cost +5 Power. Lasts 5 minutes.\\n\\n<i><size=75%>Words of Power can only be spoken once, then they lose their power.</size></i>\", \"\", \"\", \"\", True, False, False, False, False, \"\")",
            "ZOCKZECH",
            "Anemia",
            "Speaking this word weakens your body so that all attacks cost +5 Power. Lasts 5 minutes."
        },
        {
            "ProcessBook(\"You discovered a word of power!\", \"You've discovered a word of power: <sel>WHUBGLUX</sel>Speak it out loud for this effect:\\n\\n<b><size=125%>Word of Power: Fear of Water</size></b>\\nSpeaking this word causes you to hyperventilate underwater so that your breath depletes at five times its normal rate. Lasts 5 minutes.\\n\\n<i><size=75%>Words of Power can only be spoken once, then they lose their power.</size></i>\", \"\", \"\", \"\", True, False, False, False, False, \"\")",
            "WHUBGLUX",
            "Fear of Water",
            "Speaking this word causes you to hyperventilate underwater so that your breath depletes at five times its normal rate. Lasts 5 minutes."
        },
        {
            "ProcessBook(\"You discovered a word of power!\", \"You've discovered a word of power: <sel>FEAVEG</sel>Speak it out loud for this effect:\\n\\n<b><size=125%>Word of Power: Fast Swimmer</size></b>\\nSpeaking this word makes you swim much faster for five minutes.\\n\\n<i><size=75%>Words of Power can only be spoken once, then they lose their power.</size></i>\", \"\", \"\", \"\", True, False, False, False, False, \"\")",
            "FEAVEG",
            "Fast Swimmer",
            "Speaking this word makes you swim much faster for five minutes."
        },
    };

    [Theory]
    [MemberData(nameof(DiscoveryCases))]
    public void Parses_real_discovery_lines(string line, string code, string effect, string description)
    {
        var frame = WordOfPowerDiscoveredParser.TryParse(line);
        frame.Should().NotBeNull();
        frame!.Code.Should().Be(code);
        frame.EffectName.Should().Be(effect);
        frame.Description.Should().Be(description);
    }

    [Fact]
    public void Returns_null_for_unrelated_process_book()
    {
        var line = "ProcessBook(\"Skill Info\", \"Foods Consumed:\\n\\nApple: 1\", \"\", \"\", \"\", False, False, False, False, False, \"SkillReport\")";
        WordOfPowerDiscoveredParser.TryParse(line).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_unrelated_line()
    {
        WordOfPowerDiscoveredParser.TryParse("ProcessAddItem(Apple(1234), -1, True)").Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_empty_line()
    {
        WordOfPowerDiscoveredParser.TryParse("").Should().BeNull();
    }
}
