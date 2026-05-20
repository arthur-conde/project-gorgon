using FluentAssertions;
using Mithril.Tools.LogSanitizer;
using Xunit;

namespace Mithril.Tools.LogSanitizer.Tests;

public sealed class WindowsUsernameScrubberTests
{
    [Theory]
    [InlineData(@"C:\Users\arthu\AppData\Local\Project Gorgon", @"C:\Users\<USER>\AppData\Local\Project Gorgon")]
    [InlineData(@"C:\Users\PraxiUser\AppData\LocalLow\Elder Game", @"C:\Users\<USER>\AppData\LocalLow\Elder Game")]
    [InlineData(@"D:\Users\someone\proj", @"D:\Users\<USER>\proj")]
    [InlineData(@"Users\foo\bar", @"Users\<USER>\bar")]
    public void Backslash_pathsScrubbed(string input, string expected)
    {
        WindowsUsernameScrubber.Scrub(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("C:/Users/arthu/AppData/Local/PG", "C:/Users/<USER>/AppData/Local/PG")]
    [InlineData("Users/foo/bar", "Users/<USER>/bar")]
    public void ForwardSlash_pathsScrubbed(string input, string expected)
    {
        WindowsUsernameScrubber.Scrub(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("at UnityEngine.Application.Quit ()")]
    [InlineData("[D3D12 Device Filter] some message")]
    [InlineData("ProcessLoadRecipes(...)")]
    public void NonPathContent_unchanged(string input)
    {
        WindowsUsernameScrubber.Scrub(input).Should().Be(input);
    }

    [Fact]
    public void MultipleOccurrences_allScrubbed()
    {
        var input = @"copying C:\Users\foo\a.txt to C:\Users\bar\b.txt";
        var expected = @"copying C:\Users\<USER>\a.txt to C:\Users\<USER>\b.txt";

        WindowsUsernameScrubber.Scrub(input).Should().Be(expected);
    }
}
