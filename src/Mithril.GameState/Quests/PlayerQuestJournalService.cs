using Mithril.GameState.Quests.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Quests;

/// <summary>
/// Subscribes to the L1 (#550) driver's LocalPlayer pipe for quest signals
/// (<c>ProcessLoadQuests</c> bulk login + <c>ProcessBook("New Quest:" …)</c>
/// per-accept + <c>ProcessCompleteQuest</c>) and maintains the current-session
/// active journal for the active character.
///
/// <para><b>No persistence (#718).</b> Principle 13 of
/// <c>docs/world-simulator.md</c>: the world is a deterministic fold over the
/// log stream. <c>ProcessLoadQuests</c> re-fires on every login / zone
/// transition, so the active set rebuilds from the next session's replay
/// without any on-disk cache. Modules that need cross-session continuity (e.g.
/// Gandalf's repeatable-quest cooldown anchors via
/// <c>DerivedTimerProgressService</c>) maintain their own per-character
/// ledgers populated by subscribing to <see cref="PlayerQuestEvent"/>s here.</para>
///
/// <para>Threading: ingestion runs on the L1 driver's pump thread (archetype-A
/// default = <c>DeliveryContext.Inline</c>). Subscribers dispatch under the
/// same lock as the snapshot accessors — non-trivial work must hop off-thread.</para>
/// </summary>
public sealed class PlayerQuestJournalService : BackgroundService, IPlayerQuestJournalState
{
    private readonly ILogStreamDriver _driver;
    private readonly QuestJournalLoadParser _journalLoadParser;
    private readonly QuestAcceptedParser _acceptedParser;
    private readonly QuestCompletedParser _completedParser;
    private readonly IReferenceDataService _refData;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly Dictionary<string, QuestJournalEntry> _active = new(StringComparer.OrdinalIgnoreCase);

    // In-memory current-session-only set of (InternalName → last completion
    // stamp). Not exposed, not persisted, not replayed on subscribe — used
    // solely for idempotency detection: a duplicate ProcessCompleteQuest with
    // the same timestamp (driver-side replay of the same line) must not
    // re-fire Completed to subscribers. Resets to empty on Mithril restart;
    // rebuilt from each session's ProcessCompleteQuest lines during the
    // session-start log replay.
    private readonly Dictionary<string, DateTimeOffset> _completedThisSession = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Action<PlayerQuestEvent>> _handlers = [];
    private ILogSubscription? _subscription;

    public PlayerQuestJournalService(
        ILogStreamDriver driver,
        QuestJournalLoadParser journalLoadParser,
        QuestAcceptedParser acceptedParser,
        QuestCompletedParser completedParser,
        IReferenceDataService refData,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _journalLoadParser = journalLoadParser;
        _acceptedParser = acceptedParser;
        _completedParser = completedParser;
        _refData = refData;
        _diag = diag;
    }

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
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            foreach (var (_, entry) in _active)
                Invoke(handler, new PlayerQuestAccepted(entry.InternalName, entry.AcceptedAt.UtcDateTime));
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("Quests", "Subscribing to L1 driver (LocalPlayer pipe) for quest events");
        // archetype-A defaults — FromSessionStart replay + Inline delivery.
        _subscription = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                var ts = envelope.Payload.Timestamp.UtcDateTime;
                Dispatch(envelope.Payload.Data, ts);
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = "Quests",
            });

        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    private void Dispatch(string line, DateTime ts)
    {
        if (_journalLoadParser.TryParse(line, ts) is QuestJournalLoadedEvent loaded)
        {
            HandleJournalLoaded(loaded);
            return;
        }
        if (_acceptedParser.TryParse(line, ts) is QuestAcceptedEvent accepted)
        {
            HandleAccepted(accepted);
            return;
        }
        if (_completedParser.TryParse(line, ts) is QuestCompletedEvent completed)
        {
            HandleCompleted(completed);
        }
    }

    /// <summary>
    /// Apply a bulk journal-load observation: snapshot-replace the active set
    /// with every quest the server reports as currently in the player's
    /// journal. Resolves int ids → InternalNames via reference data; unknown
    /// ids are dropped silently (game-data drift). Fires
    /// <see cref="PlayerQuestAccepted"/> for newly-present entries and
    /// <see cref="PlayerQuestAbandoned"/> for entries that were active before
    /// but aren't anymore — a real inference from a real log event, stamped on
    /// the <see cref="QuestJournalLoadedEvent.Timestamp"/>.
    /// </summary>
    private void HandleJournalLoaded(QuestJournalLoadedEvent loaded)
    {
        var stamp = new DateTimeOffset(loaded.Timestamp, TimeSpan.Zero);
        var newActive = new Dictionary<string, QuestJournalEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in loaded.WorkOrderQuestIds)
            if (_refData.Quests.TryGetValue($"quest_{id}", out var q) && !string.IsNullOrEmpty(q.InternalName))
                newActive[q.InternalName] = new QuestJournalEntry(q.InternalName, stamp);
        foreach (var id in loaded.RegularQuestIds)
            if (_refData.Quests.TryGetValue($"quest_{id}", out var q) && !string.IsNullOrEmpty(q.InternalName))
                newActive[q.InternalName] = new QuestJournalEntry(q.InternalName, stamp);

        var events = new List<PlayerQuestEvent>();
        lock (_lock)
        {
            foreach (var (name, _) in _active)
                if (!newActive.ContainsKey(name))
                    events.Add(new PlayerQuestAbandoned(name, loaded.Timestamp));
            foreach (var (name, entry) in newActive)
                if (!_active.ContainsKey(name))
                    events.Add(new PlayerQuestAccepted(entry.InternalName, entry.AcceptedAt.UtcDateTime));

            if (events.Count > 0)
            {
                _active.Clear();
                foreach (var (name, entry) in newActive) _active[name] = entry;
                FireAll(events);
            }
        }
    }

    private void HandleAccepted(QuestAcceptedEvent ev)
    {
        if (string.IsNullOrEmpty(ev.QuestInternalName)) return;
        var stamp = new DateTimeOffset(ev.Timestamp, TimeSpan.Zero);
        lock (_lock)
        {
            if (!_active.ContainsKey(ev.QuestInternalName))
            {
                _active[ev.QuestInternalName] = new QuestJournalEntry(ev.QuestInternalName, stamp);
                FireOne(new PlayerQuestAccepted(ev.QuestInternalName, ev.Timestamp));
            }
        }
    }

    private void HandleCompleted(QuestCompletedEvent ev)
    {
        if (string.IsNullOrEmpty(ev.QuestInternalName)) return;
        var stamp = new DateTimeOffset(ev.Timestamp, TimeSpan.Zero);
        lock (_lock)
        {
            // Idempotency: a replayed ProcessCompleteQuest line carries the
            // same Timestamp. Don't re-fire Completed (downstream cooldown
            // anchors are past-anchored and the duplicate would no-op anyway,
            // but more importantly we don't want subscribers to think a fresh
            // turn-in just happened). The map is in-memory current-session
            // only — not exposed, not persisted, not replayed on subscribe.
            bool isNew = !_completedThisSession.TryGetValue(ev.QuestInternalName, out var prior)
                         || prior != stamp;
            _active.Remove(ev.QuestInternalName);

            if (isNew)
            {
                _completedThisSession[ev.QuestInternalName] = stamp;
                FireOne(new PlayerQuestCompleted(ev.QuestInternalName, ev.Timestamp));
            }
        }
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void FireOne(PlayerQuestEvent ev)
    {
        foreach (var h in _handlers) Invoke(h, ev);
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void FireAll(IReadOnlyList<PlayerQuestEvent> events)
    {
        foreach (var ev in events)
            foreach (var h in _handlers)
                Invoke(h, ev);
    }

    private void Invoke(Action<PlayerQuestEvent> handler, PlayerQuestEvent ev)
    {
        try { handler(ev); }
        catch (Exception ex) { _diag?.Warn("Quests", $"Subscriber threw: {ex.Message}"); }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Dispose();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly PlayerQuestJournalService _svc;
        private Action<PlayerQuestEvent>? _handler;
        public Subscription(PlayerQuestJournalService svc, Action<PlayerQuestEvent> handler) { _svc = svc; _handler = handler; }
        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handler, null);
            if (h is null) return;
            lock (_svc._lock) _svc._handlers.Remove(h);
        }
    }
}
