using Mithril.GameState.Pins;

namespace Legolas.Tests;

/// <summary>
/// Test double for <see cref="IPlayerPinTracker"/> (#468). Lets a test seed a
/// pre-existing area set (no event — mimics login replay already folded by
/// the real service) and push live Added/Removed/AreaChanged notifications.
/// <see cref="Subscribe"/> replays a Snapshot synchronously like the real one.
/// </summary>
public sealed class FakePlayerPinTracker : IPlayerPinTracker
{
    private readonly List<Action<PinSetChanged>> _handlers = new();
    private readonly List<MapPin> _pins = new();

    public string? CurrentArea { get; set; } = "AreaTest";
    public IReadOnlyList<MapPin> CurrentAreaPins => _pins.ToArray();

    public IDisposable Subscribe(Action<PinSetChanged> handler)
    {
        handler(new PinSetChanged(
            PinSetChange.Snapshot, CurrentArea, null, CurrentAreaPins, DateTimeOffset.UtcNow));
        _handlers.Add(handler);
        return new Sub(this, handler);
    }

    /// <summary>Pre-existing pins present before the consumer subscribes /
    /// arms — the existing-pins route's input. Raises no event.</summary>
    public void SeedExisting(params MapPin[] pins)
    {
        _pins.Clear();
        _pins.AddRange(pins);
    }

    public static MapPin Pin(double x, double z, string label = "",
        PinShape shape = PinShape.Dot, PinColor color = PinColor.White) =>
        new(x, z, label, shape, color, 1);

    /// <summary>A genuinely-new pin drop (turn-order route input).</summary>
    public MapPin Add(double x, double z, string label = "")
    {
        var p = Pin(x, z, label);
        _pins.Add(p);
        Raise(new PinSetChanged(PinSetChange.Added, CurrentArea, p, CurrentAreaPins, DateTimeOffset.UtcNow));
        return p;
    }

    public void Remove(MapPin p)
    {
        _pins.Remove(p);
        Raise(new PinSetChanged(PinSetChange.Removed, CurrentArea, p, CurrentAreaPins, DateTimeOffset.UtcNow));
    }

    public void ChangeArea(string? area)
    {
        CurrentArea = area;
        _pins.Clear();
        Raise(new PinSetChanged(PinSetChange.AreaChanged, area, null, CurrentAreaPins, DateTimeOffset.UtcNow));
    }

    private void Raise(PinSetChanged n)
    {
        foreach (var h in _handlers.ToArray()) h(n);
    }

    private sealed class Sub(FakePlayerPinTracker owner, Action<PinSetChanged> handler) : IDisposable
    {
        public void Dispose() => owner._handlers.Remove(handler);
    }
}
