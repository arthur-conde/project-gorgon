using Mithril.GameState.Quests;

namespace Mithril.TestSupport;

/// <summary>
/// Minimal <see cref="IPlayerQuestJournalService"/> stand-in for tests that need to
/// drive <c>QuestSource</c> / quest-aware modules without spinning up the real
/// log-tailing service. Exposes <see cref="RaiseAccepted"/> /
/// <see cref="RaiseAbandoned"/> / <see cref="RaiseCompleted"/> to mutate the
/// internal maps and fan out the matching <see cref="QuestEvent"/> to
/// subscribers — same shape as the real service's per-event dispatch path.
/// Subscribe atomically replays current state, mirroring the real
/// <see cref="PlayerQuestJournalService"/>.
/// </summary>
internal sealed class FakePlayerQuestJournalService : IPlayerQuestJournalService
{
    private readonly Dictionary<string, QuestJournalEntry> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuestCompletionState> _completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action<QuestEvent>> _handlers = [];
    private readonly object _lock = new();

    public IReadOnlyDictionary<string, QuestJournalEntry> ActiveQuests
    {
        get { lock (_lock) return new Dictionary<string, QuestJournalEntry>(_active, StringComparer.OrdinalIgnoreCase); }
    }

    public IReadOnlyDictionary<string, QuestCompletionState> CompletionHistory
    {
        get { lock (_lock) return new Dictionary<string, QuestCompletionState>(_completed, StringComparer.OrdinalIgnoreCase); }
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

    public bool TryGetCompletion(string internalName, out QuestCompletionState state)
    {
        lock (_lock)
        {
            if (_completed.TryGetValue(internalName, out var found)) { state = found; return true; }
            state = null!;
            return false;
        }
    }

    public IDisposable Subscribe(Action<QuestEvent> handler)
    {
        lock (_lock)
        {
            foreach (var (_, entry) in _active)
                handler(new QuestEvent(QuestEventKind.Accepted, entry.InternalName, entry.AcceptedAt.UtcDateTime));
            foreach (var (_, c) in _completed)
                handler(new QuestEvent(QuestEventKind.Completed, c.InternalName, c.LastCompletedAt.UtcDateTime));
            _handlers.Add(handler);
        }
        return new Subscription(this, handler);
    }

    public void RaiseAccepted(string internalName, DateTime timestamp)
    {
        QuestEvent ev = new(QuestEventKind.Accepted, internalName, timestamp);
        lock (_lock)
        {
            _active[internalName] = new QuestJournalEntry(internalName, new DateTimeOffset(timestamp, TimeSpan.Zero));
            FireAll(ev);
        }
    }

    public void RaiseAbandoned(string internalName, DateTime timestamp)
    {
        QuestEvent ev = new(QuestEventKind.Abandoned, internalName, timestamp);
        lock (_lock)
        {
            _active.Remove(internalName);
            FireAll(ev);
        }
    }

    public void RaiseCompleted(string internalName, DateTime timestamp)
    {
        QuestEvent ev = new(QuestEventKind.Completed, internalName, timestamp);
        lock (_lock)
        {
            _active.Remove(internalName);
            _completed[internalName] = new QuestCompletionState(internalName, new DateTimeOffset(timestamp, TimeSpan.Zero));
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

            var events = new List<QuestEvent>();
            foreach (var (name, _) in _active)
                if (!newSet.ContainsKey(name))
                    events.Add(new QuestEvent(QuestEventKind.Abandoned, name, timestamp));
            foreach (var (name, entry) in newSet)
                if (!_active.ContainsKey(name))
                    events.Add(new QuestEvent(QuestEventKind.Accepted, name, entry.AcceptedAt.UtcDateTime));

            _active.Clear();
            foreach (var (k, v) in newSet) _active[k] = v;
            foreach (var ev in events) FireAll(ev);
        }
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void FireAll(QuestEvent ev)
    {
        foreach (var h in _handlers) h(ev);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly FakePlayerQuestJournalService _svc;
        private Action<QuestEvent>? _handler;
        public Subscription(FakePlayerQuestJournalService svc, Action<QuestEvent> handler) { _svc = svc; _handler = handler; }
        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handler, null);
            if (h is null) return;
            lock (_svc._lock) _svc._handlers.Remove(h);
        }
    }
}
