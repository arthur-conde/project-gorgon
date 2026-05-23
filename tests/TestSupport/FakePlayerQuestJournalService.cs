using Mithril.GameState.Quests;

namespace Mithril.TestSupport;

/// <summary>
/// Minimal <see cref="IPlayerQuestJournalState"/> stand-in for tests that need
/// to drive <c>QuestSource</c> / quest-aware modules without spinning up the
/// real log-tailing service. Exposes <see cref="RaiseAccepted"/> /
/// <see cref="RaiseAbandoned"/> / <see cref="RaiseCompleted"/> to mutate the
/// internal map and fan out the matching <see cref="PlayerQuestEvent"/>
/// subtype to subscribers — same shape as the real service's per-event
/// dispatch path. Subscribe atomically replays the current active set as
/// <see cref="PlayerQuestAccepted"/> events, mirroring the real
/// <c>PlayerQuestJournalService</c> post-#718.
/// </summary>
internal sealed class FakePlayerQuestJournalService : IPlayerQuestJournalState
{
    private readonly Dictionary<string, QuestJournalEntry> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action<PlayerQuestEvent>> _handlers = [];
    private readonly object _lock = new();

    public IReadOnlyDictionary<string, QuestJournalEntry> ActiveQuests
    {
        get { lock (_lock) return new Dictionary<string, QuestJournalEntry>(_active, StringComparer.OrdinalIgnoreCase); }
    }

    public bool TryGetActive(string internalName, out QuestJournalEntry entry)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(internalName, out var found)) { entry = found; return true; }
            entry = null!;
            return false;
        }
    }

    public IDisposable Subscribe(Action<PlayerQuestEvent> handler)
    {
        lock (_lock)
        {
            foreach (var (_, entry) in _active)
                handler(new PlayerQuestAccepted(entry.InternalName, entry.AcceptedAt.UtcDateTime));
            _handlers.Add(handler);
        }
        return new Subscription(this, handler);
    }

    public void RaiseAccepted(string internalName, DateTime timestamp)
    {
        var ev = new PlayerQuestAccepted(internalName, timestamp);
        lock (_lock)
        {
            _active[internalName] = new QuestJournalEntry(internalName, new DateTimeOffset(timestamp, TimeSpan.Zero));
            FireAll(ev);
        }
    }

    public void RaiseAbandoned(string internalName, DateTime timestamp)
    {
        var ev = new PlayerQuestAbandoned(internalName, timestamp);
        lock (_lock)
        {
            _active.Remove(internalName);
            FireAll(ev);
        }
    }

    public void RaiseCompleted(string internalName, DateTime timestamp)
    {
        var ev = new PlayerQuestCompleted(internalName, timestamp);
        lock (_lock)
        {
            _active.Remove(internalName);
            FireAll(ev);
        }
    }

    /// <summary>
    /// Bulk journal-load helper that mimics what the real service does on a
    /// <c>ProcessLoadQuests</c> line: snapshot-replace the active set and fire
    /// per-quest Accepted/Abandoned diff events.
    /// </summary>
    public void RaiseJournalLoaded(IReadOnlyList<string> activeInternalNames, DateTime timestamp)
    {
        lock (_lock)
        {
            var stamp = new DateTimeOffset(timestamp, TimeSpan.Zero);
            var newSet = new Dictionary<string, QuestJournalEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in activeInternalNames)
                newSet[name] = new QuestJournalEntry(name, stamp);

            var events = new List<PlayerQuestEvent>();
            foreach (var (name, _) in _active)
                if (!newSet.ContainsKey(name))
                    events.Add(new PlayerQuestAbandoned(name, timestamp));
            foreach (var (name, entry) in newSet)
                if (!_active.ContainsKey(name))
                    events.Add(new PlayerQuestAccepted(entry.InternalName, entry.AcceptedAt.UtcDateTime));

            _active.Clear();
            foreach (var (k, v) in newSet) _active[k] = v;
            foreach (var ev in events) FireAll(ev);
        }
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void FireAll(PlayerQuestEvent ev)
    {
        foreach (var h in _handlers) h(ev);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly FakePlayerQuestJournalService _svc;
        private Action<PlayerQuestEvent>? _handler;
        public Subscription(FakePlayerQuestJournalService svc, Action<PlayerQuestEvent> handler) { _svc = svc; _handler = handler; }
        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handler, null);
            if (h is null) return;
            lock (_svc._lock) _svc._handlers.Remove(h);
        }
    }
}
