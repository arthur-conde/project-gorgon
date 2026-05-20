using FluentAssertions;
using Mithril.Tools.LogSanitizer;
using Xunit;

namespace Mithril.Tools.LogSanitizer.Tests;

public sealed class LogSanitizerTests
{
    [Fact]
    public void Sanitize_replacesOwnCharacterAndPlayer_andScrubsPath()
    {
        var input =
            """
            [20:01:14] Logged in as character Daedric. Time UTC=05/19/2026 20:01:14. Timezone Offset 01:00:00
            [12:34:56] LocalPlayer: ProcessAddPlayer(-1, 2, "@a", "Alice", "x")
            [12:34:57] LocalPlayer: ProcessAddPlayer(-2, 2, "@a", "Daedric", "x")
            [12:34:58] LocalPlayer: Sells item to Alice
            InvalidOperationException at C:\Users\arthu\AppData\Local\PG\foo.dll
            """;

        var output = new LogSanitizer(new PlayerLogRules()).Sanitize(input);

        output.Should().Contain("Logged in as character <CHARACTER>.");
        output.Should().Contain("\"<PLAYER_1>\"");  // Alice replaced inside the quoted 4th arg
        output.Should().Contain("\"<CHARACTER>\""); // Daedric replaced inside the quoted 4th arg of line 3
        output.Should().Contain("Sells item to <PLAYER_1>");
        output.Should().Contain(@"C:\Users\<USER>\AppData\Local\PG\foo.dll");
        output.Should().NotContain("Daedric");
        output.Should().NotContain("Alice");
        output.Should().NotContain(@"\arthu\");
    }

    [Fact]
    public void Sanitize_isIdempotent()
    {
        var input =
            """
            [20:01:14] Logged in as character Daedric. Time UTC=05/19/2026 20:01:14
            [12:34:56] LocalPlayer: ProcessAddPlayer(-1, 2, "@a", "Alice", "x")
            InvalidOperationException at C:\Users\arthu\foo.dll
            """;

        var sanitizer = new LogSanitizer(new PlayerLogRules());
        var first = sanitizer.Sanitize(input);
        var second = sanitizer.Sanitize(first);

        second.Should().Be(first);
    }

    [Fact]
    public void Sanitize_preservesLineCountAndOrdering()
    {
        var input =
            """
            line 1
            line 2
            line 3
            """;

        var output = new LogSanitizer(new PlayerLogRules()).Sanitize(input);
        var inputLines = input.Split('\n');
        var outputLines = output.Split('\n');

        outputLines.Should().HaveCount(inputLines.Length);
    }

    [Fact]
    public void Sanitize_emptyInput_emptyOutput()
    {
        var output = new LogSanitizer(new PlayerLogRules()).Sanitize(string.Empty);

        output.Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_substringName_doesNotClobberLongerName()
    {
        // Bob is a substring of Bobby. Without length-descending replacement order, Bob would be
        // replaced first and mangle Bobby into "<PLAYER_1>by", and the Bobby → <PLAYER_2> replacement
        // would no longer match — leaking the real name "by" remnant into the sanitized output.
        var input =
            """
            [12:34:56] LocalPlayer: ProcessAddPlayer(-1, 2, "@a", "Bob", "x")
            [12:34:57] LocalPlayer: ProcessAddPlayer(-2, 2, "@a", "Bobby", "x")
            [12:34:58] LocalPlayer: Sells item to Bobby
            [12:34:59] LocalPlayer: Sells item to Bob
            """;

        var output = new LogSanitizer(new PlayerLogRules()).Sanitize(input);

        output.Should().Contain("\"<PLAYER_1>\"");
        output.Should().Contain("\"<PLAYER_2>\"");
        output.Should().Contain("Sells item to <PLAYER_2>");
        output.Should().Contain("Sells item to <PLAYER_1>");
        output.Should().NotContain("Bob");
        output.Should().NotContain("Bobby");
        output.Should().NotContain("<PLAYER_1>by");
    }
}
