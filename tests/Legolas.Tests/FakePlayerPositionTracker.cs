using Mithril.GameState.Movement;

namespace Legolas.Tests;

/// <summary>
/// Test double for <see cref="IPlayerPositionTracker"/> (#461/#465). Lets a
/// test seed a fix before the consumer subscribes (replayed synchronously on
/// <see cref="Subscribe"/>, like the real tracker's replay-on-subscribe) and
/// push later fixes to simulate a zone-in / teleport.
/// </summary>
public sealed class FakePlayerPositionTracker : IPlayerPositionTracker
{
    private readonly List<Action<PlayerPosition>> _handlers = new();

    public PlayerPosition? Current { get; private set; }

    public IDisposable Subscribe(Action<PlayerPosition> handler)
    {
        if (Current is { } c) handler(c);
        _handlers.Add(handler);
        return new Sub(this, handler);
    }

    /// <summary>Seed a fix with no notification (pre-subscribe state).</summary>
    public void Seed(double x, double y, double z,
        PlayerPositionSource source = PlayerPositionSource.Spawn, DateTimeOffset? measuredAt = null)
        => Current = new PlayerPosition(x, y, z, measuredAt ?? DateTimeOffset.UtcNow, source);

    /// <summary>A new fix (zone-in / teleport) — notifies live subscribers.</summary>
    public void Push(double x, double y, double z,
        PlayerPositionSource source = PlayerPositionSource.Movement, DateTimeOffset? measuredAt = null)
    {
        Current = new PlayerPosition(x, y, z, measuredAt ?? DateTimeOffset.UtcNow, source);
        foreach (var h in _handlers.ToArray()) h(Current);
    }

    private sealed class Sub(FakePlayerPositionTracker owner, Action<PlayerPosition> handler) : IDisposable
    {
        public void Dispose() => owner._handlers.Remove(handler);
    }
}
