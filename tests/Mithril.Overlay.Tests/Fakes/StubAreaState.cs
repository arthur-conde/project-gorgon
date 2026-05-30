using Arda.World.Player;

namespace Mithril.Overlay.Tests.Fakes;

/// <summary>Mutable test stub for <see cref="IAreaState"/>. The scene-hook
/// tests flip <see cref="CurrentArea"/> to exercise the uncalibrated-area
/// gate and area-key plumbing into <see cref="IOverlaySceneContext"/>.</summary>
internal sealed class StubAreaState : IAreaState
{
    public string? CurrentArea { get; set; }
}

/// <summary>Minimal <see cref="IPositionState"/> stub. The scene-hook
/// tests don't read player position; the service requires the dependency
/// to satisfy the Decision-C consumption-side ctor shape.</summary>
internal sealed class StubPositionState : IPositionState
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
}
