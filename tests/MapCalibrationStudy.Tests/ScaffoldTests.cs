using FluentAssertions;
using Mithril.Tools.MapCalibrationStudy;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Test_project_builds_and_runs()
    {
        typeof(OrientationClass).Assembly.Should().NotBeNull();
    }
}
