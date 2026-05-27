using Arda.World.Player;

namespace Gandalf.Tests;

/// <summary>
/// Mutable <see cref="IAreaState"/> stub for Gandalf tests. Exposes
/// <see cref="SetArea"/> so tests can drive area transitions.
/// </summary>
internal sealed class FakePlayerAreaState : IAreaState
{
    public string? CurrentArea { get; private set; }

    public void SetArea(string? area) => CurrentArea = area;
}
