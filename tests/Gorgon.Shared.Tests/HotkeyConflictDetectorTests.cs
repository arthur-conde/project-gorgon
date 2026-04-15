using FluentAssertions;
using Gorgon.Shared.Hotkeys;
using Xunit;

namespace Gorgon.Shared.Tests;

public class HotkeyConflictDetectorTests
{
    [Fact]
    public void NoConflicts_WhenAllUnique()
    {
        var bindings = new[]
        {
            new HotkeyBinding("a", 65, HotkeyModifiers.Ctrl),
            new HotkeyBinding("b", 66, HotkeyModifiers.Ctrl),
        };
        HotkeyConflictDetector.Detect(bindings).Should().BeEmpty();
    }

    [Fact]
    public void Detects_BothSidesOfCollision()
    {
        var bindings = new[]
        {
            new HotkeyBinding("a", 65, HotkeyModifiers.Ctrl),
            new HotkeyBinding("b", 65, HotkeyModifiers.Ctrl),
        };
        var result = HotkeyConflictDetector.Detect(bindings);
        result.Should().ContainKey("a").WhoseValue.ConflictingCommandId.Should().Be("b");
        result.Should().ContainKey("b").WhoseValue.ConflictingCommandId.Should().Be("a");
    }

    [Fact]
    public void CheckProposed_ExcludesSelf()
    {
        var existing = new[] { new HotkeyBinding("a", 65, HotkeyModifiers.Ctrl) };
        HotkeyConflictDetector.CheckProposed(existing, 65, HotkeyModifiers.Ctrl, "a").Should().BeNull();
        HotkeyConflictDetector.CheckProposed(existing, 65, HotkeyModifiers.Ctrl, "b")!
            .ConflictingCommandId.Should().Be("a");
    }

    [Fact]
    public void DifferentModifiers_AreNotConflicting()
    {
        var bindings = new[]
        {
            new HotkeyBinding("a", 65, HotkeyModifiers.Ctrl),
            new HotkeyBinding("b", 65, HotkeyModifiers.Alt),
        };
        HotkeyConflictDetector.Detect(bindings).Should().BeEmpty();
    }
}
