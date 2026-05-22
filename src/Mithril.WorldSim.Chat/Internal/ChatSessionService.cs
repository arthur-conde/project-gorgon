namespace Mithril.WorldSim.Chat.Internal;

/// <summary>
/// Concrete <see cref="IChatSessionService"/>. Writeable via
/// <see cref="Update"/> — called by <see cref="Producers.ChatLogProducer"/>
/// every time a banner line flows through the producer's envelope stream.
/// Read-side via <see cref="Current"/> + <see cref="Subscribe"/> — handlers
/// fire synchronously on the producer's emit thread (= the world's merger
/// thread once the producer is plugged into the world).
/// </summary>
internal sealed class ChatSessionService : IChatSessionService
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subs = new();
    private ChatSession? _current;

    public ChatSession? Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    public IDisposable Subscribe(Action<ChatSession> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var sub = new Subscription(handler, this);
        lock (_gate) _subs.Add(sub);
        return sub;
    }

    /// <summary>
    /// Replace <see cref="Current"/> with the supplied session and notify
    /// subscribers. Idempotent against value-equal sessions: a repeat call
    /// with the same record short-circuits without notifying. This keeps
    /// replay drain from firing N redundant notifications when the same
    /// banner is encountered repeatedly (PG only writes one banner per
    /// login, but defending against bug-class duplicates is cheap).
    /// </summary>
    public void Update(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Subscription[] snapshot;
        lock (_gate)
        {
            if (_current is not null && _current.Equals(session)) return;
            _current = session;
            snapshot = _subs.ToArray();
        }

        foreach (var sub in snapshot)
        {
            if (sub.IsDisposed) continue;
            sub.Invoke(session);
        }
    }

    private void Remove(Subscription sub)
    {
        lock (_gate) _subs.Remove(sub);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action<ChatSession> _handler;
        private readonly ChatSessionService _owner;

        public Subscription(Action<ChatSession> handler, ChatSessionService owner)
        {
            _handler = handler;
            _owner = owner;
        }

        public bool IsDisposed { get; private set; }

        public void Invoke(ChatSession session) => _handler(session);

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            _owner.Remove(this);
        }
    }
}
