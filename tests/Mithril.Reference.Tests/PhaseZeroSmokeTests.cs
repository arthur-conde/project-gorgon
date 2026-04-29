using FluentAssertions;
using Xunit;

namespace Mithril.Reference.Tests;

/// <summary>
/// Phase 0 placeholder. Confirms the project skeleton compiles, references the
/// Mithril.Reference assembly, and has working xunit + FluentAssertions wiring.
/// Replace once Phase 1 (Quest POCOs + bridge primitives) lands.
/// </summary>
public class PhaseZeroSmokeTests
{
    [Fact]
    public void Skeleton_Compiles_And_Tests_Run()
    {
        true.Should().BeTrue();
    }
}
