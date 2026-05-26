using Arda.World.Player;
using Mithril.GameState.Areas;

namespace Gandalf.Tests;

/// <summary>
/// Test double that implements both <see cref="IPlayerAreaState"/> (legacy)
/// and <see cref="IAreaState"/> (Arda). Post-migration consumers inject
/// <see cref="IAreaState"/>; the legacy interface is retained for any
/// remaining GameState tests that still need it.
/// </summary>
internal sealed class FakePlayerAreaState : IPlayerAreaState, IAreaState
{
    private readonly object _lock = new();
    private readonly List<Action<PlayerAreaChanged>> _handlers = new();
    private string? _current;

    public string? CurrentArea
    {
        get { lock (_lock) return _current; }
    }

    public IDisposable Subscribe(Action<PlayerAreaChanged> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            handler(new PlayerAreaChanged(
                PlayerAreaChangeKind.Snapshot, Previous: null, Current: _current,
                At: DateTimeOffset.MinValue));
            _handlers.Add(handler);
            return new Sub(this, handler);
        }
    }

    public void SetArea(string? area, DateTimeOffset? at = null)
    {
        Action<PlayerAreaChanged>[] toFire;
        PlayerAreaChanged change;
        lock (_lock)
        {
            if (_current == area) return;
            var prev = _current;
            _current = area;
            change = new PlayerAreaChanged(
                PlayerAreaChangeKind.Changed, prev, area, at ?? DateTimeOffset.UtcNow);
            toFire = _handlers.ToArray();
        }
        foreach (var h in toFire) h(change);
    }

    private sealed class Sub : IDisposable
    {
        private FakePlayerAreaState? _owner;
        private readonly Action<PlayerAreaChanged> _handler;
        public Sub(FakePlayerAreaState owner, Action<PlayerAreaChanged> handler)
        {
            _owner = owner;
            _handler = handler;
        }
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._lock) { owner._handlers.Remove(_handler); }
        }
    }
}
