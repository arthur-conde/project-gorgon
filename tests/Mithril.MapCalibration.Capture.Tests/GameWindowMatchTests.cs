using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class GameWindowMatchTests
{
    [Theory]
    [InlineData("ProjectGorgon", "ProjectGorgon", true)]
    [InlineData("ProjectGorgon", "ProjectGorgon64", true)]
    [InlineData("ProjectGorgon", "Project Gorgon", false)]   // process image name has no space
    [InlineData("ProjectGorgon", "chrome", false)]
    [InlineData("", "ProjectGorgon", false)]                  // unconfigured → no match
    public void Process_name_match_mirrors_focus_gate(string configured, string processName, bool expected)
        => Win32GameWindowLocator.ProcessNameMatches(configured, processName).Should().Be(expected);
}
