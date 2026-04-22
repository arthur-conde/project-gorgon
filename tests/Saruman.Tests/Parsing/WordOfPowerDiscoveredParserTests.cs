using FluentAssertions;
using Saruman.Domain;
using Saruman.Parsing;
using Xunit;

namespace Saruman.Tests.Parsing;

public sealed class WordOfPowerDiscoveredParserTests
{
    private readonly WordOfPowerDiscoveredParser _parser = new();

    // Five concrete lines captured from Player.log on 2026-04-22.
    public static TheoryData<string, string, string, string> DiscoveryCases() => new()
    {
        {
            "[02:02:56] LocalPlayer: ProcessBook(\"You discovered a word of power!\", \"You've discovered a word of power: <sel>ZOCKZECH</sel>Speak it out loud for this effect:\\n\\n<b><size=125%>Word of Power: Anemia</size></b>\\nSpeaking this word weakens your body so that all attacks cost +5 Power. Lasts 5 minutes.\\n\\n<i><size=75%>Words of Power can only be spoken once, then they lose their power. Write this Word down, so you can use it later.\\n\\nYou can only benefit from the effects of two Words of Power simultaneously.</size></i>\", \"\", \"\", \"\", True, False, False, False, False, \"\")",
            "ZOCKZECH",
            "Anemia",
            "Speaking this word weakens your body so that all attacks cost +5 Power. Lasts 5 minutes."
        },
        {
            "[02:02:57] LocalPlayer: ProcessBook(\"You discovered a word of power!\", \"You've discovered a word of power: <sel>WHUBGLUX</sel>Speak it out loud for this effect:\\n\\n<b><size=125%>Word of Power: Fear of Water</size></b>\\nSpeaking this word causes you to hyperventilate underwater so that your breath depletes at five times its normal rate. Lasts 5 minutes.\\n\\n<i><size=75%>Words of Power can only be spoken once, then they lose their power. Write this Word down, so you can use it later.\\n\\nYou can only benefit from the effects of two Words of Power simultaneously.</size></i>\", \"\", \"\", \"\", True, False, False, False, False, \"\")",
            "WHUBGLUX",
            "Fear of Water",
            "Speaking this word causes you to hyperventilate underwater so that your breath depletes at five times its normal rate. Lasts 5 minutes."
        },
        {
            "[02:02:58] LocalPlayer: ProcessBook(\"You discovered a word of power!\", \"You've discovered a word of power: <sel>FEAVEG</sel>Speak it out loud for this effect:\\n\\n<b><size=125%>Word of Power: Fast Swimmer</size></b>\\nSpeaking this word makes you swim much faster for five minutes.\\n\\n<i><size=75%>Words of Power can only be spoken once, then they lose their power. Write this Word down, so you can use it later.\\n\\nYou can only benefit from the effects of two Words of Power simultaneously.</size></i>\", \"\", \"\", \"\", True, False, False, False, False, \"\")",
            "FEAVEG",
            "Fast Swimmer",
            "Speaking this word makes you swim much faster for five minutes."
        },
        {
            "[02:02:58] LocalPlayer: ProcessBook(\"You discovered a word of power!\", \"You've discovered a word of power: <sel>HOIMWOB</sel>Speak it out loud for this effect:\\n\\n<b><size=125%>Word of Power: Increased Inventory</size></b>\\nSpeaking this word increases your inventory size by 10 for an hour.\\n\\n<i><size=75%>Words of Power can only be spoken once, then they lose their power. Write this Word down, so you can use it later.\\n\\nYou can only benefit from the effects of two Words of Power simultaneously.</size></i>\", \"\", \"\", \"\", True, False, False, False, False, \"\")",
            "HOIMWOB",
            "Increased Inventory",
            "Speaking this word increases your inventory size by 10 for an hour."
        },
        {
            "[02:02:59] LocalPlayer: ProcessBook(\"You discovered a word of power!\", \"You've discovered a word of power: <sel>TECKPLUE</sel>Speak it out loud for this effect:\\n\\n<b><size=125%>Word of Power: Cure Bovinity</size></b>\\nSpeaking this word causes you to stop being a cow if you are one.\\n\\n<i><size=75%>Words of Power can only be spoken once, then they lose their power. Write this Word down, so you can use it later.\\n\\nYou can only benefit from the effects of two Words of Power simultaneously.</size></i>\", \"\", \"\", \"\", True, False, False, False, False, \"\")",
            "TECKPLUE",
            "Cure Bovinity",
            "Speaking this word causes you to stop being a cow if you are one."
        },
    };

    [Theory]
    [MemberData(nameof(DiscoveryCases))]
    public void Parses_real_discovery_lines(string line, string code, string effect, string description)
    {
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<WordOfPowerDiscovered>();
        var d = (WordOfPowerDiscovered)evt!;
        d.Code.Should().Be(code);
        d.EffectName.Should().Be(effect);
        d.Description.Should().Be(description);
    }

    [Fact]
    public void Returns_null_for_unrelated_process_book()
    {
        var line = "[10:00:00] LocalPlayer: ProcessBook(\"Skill Info\", \"Foods Consumed:\\n\\nApple: 1\", \"\", \"\", \"\", False, False, False, False, False, \"SkillReport\")";
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_unrelated_line()
    {
        _parser.TryParse("LocalPlayer: ProcessAddItem(Apple(1234), -1, True)", DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_empty_line()
    {
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
    }
}
