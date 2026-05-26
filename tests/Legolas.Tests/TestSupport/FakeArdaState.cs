using Arda.World.Player;

namespace Legolas.Tests.TestSupport;

/// <summary>Minimal <see cref="IAreaState"/> test double.</summary>
internal sealed class FakeAreaState : IAreaState
{
    public string? CurrentArea { get; set; }
}

/// <summary>Minimal <see cref="IPositionState"/> test double.</summary>
internal sealed class FakePositionState : IPositionState
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
}

/// <summary>Minimal <see cref="IMapPinState"/> test double.</summary>
internal sealed class FakeMapPinState : IMapPinState
{
    private readonly List<MapPinEntry> _pins = new();

    public IReadOnlyCollection<MapPinEntry> Pins => _pins.AsReadOnly();

    public void Add(MapPinEntry pin) => _pins.Add(pin);

    public void Remove(double x, double z)
    {
        _pins.RemoveAll(p => p.X == x && p.Z == z);
    }

    public void Clear() => _pins.Clear();
}
