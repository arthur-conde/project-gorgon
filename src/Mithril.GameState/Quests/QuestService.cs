using Mithril.GameState.Quests.Parsing;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Quests;

/// <summary>
/// Subscribes to the L1 (#550) driver's LocalPlayer pipe for quest signals
/// (<c>ProcessLoadQuests</c> bulk login + <c>ProcessBook("New Quest:" …)</c>
/// per-accept + <c>ProcessCompleteQuest</c>) and maintains the per-character
/// active journal + completion history. Mirrors the
/// <see cref="Mithril.GameState.Inventory.IInventoryService"/> pattern: lock +
/// atomic replay on <see cref="Subscribe"/> closes the late-attach race.
///
/// Persistence: per-character JSON at <c>characters/{slug}/quests.json</c> via
/// <see cref="PerCharacterView{T}"/>. <see cref="ActiveQuests"/> is rebuildable
/// from the next <c>ProcessLoadQuests</c> log line, but
/// <see cref="CompletionHistory"/> needs disk so cooldown anchors survive
/// restarts (a quest completed three days ago has no log line in today's
/// session). Saving on each mutation; quest events arrive at most every few
/// seconds, no debouncing needed.
///
/// Threading: ingestion runs on the L1 driver's pump thread (archetype-A
/// default = <c>DeliveryContext.Inline</c>), character-switch reloads run on
/// the <see cref="PerCharacterView{T}.CurrentChanged"/> callback thread
/// (typically the active-character service thread). Both dispatch to
/// subscribers under the same lock as the snapshot accessors — subscribers
/// must dispatch off-thread for non-trivial work.
/// </summary>
public sealed class QuestService : BackgroundService, IQuestService
{
    private readonly ILogStreamDriver _driver;
    private readonly QuestJournalLoadParser _journalLoadParser;
    private readonly QuestAcceptedParser _acceptedParser;
    private readonly QuestCompletedParser _completedParser;
    private readonly PerCharacterView<QuestServiceState> _view;
    private readonly IReferenceDataService _refData;
    private readonly TimeProvider _time;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly Dictionary<string, QuestJournalEntry> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuestCompletionState> _completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action<QuestEvent>> _handlers = [];
    private ILogSubscription? _subscription;

    public QuestService(
        ILogStreamDriver driver,
        QuestJournalLoadParser journalLoadParser,
        QuestAcceptedParser acceptedParser,
        QuestCompletedParser completedParser,
        PerCharacterView<QuestServiceState> view,
        IReferenceDataService refData,
        TimeProvider? time = null,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _journalLoadParser = journalLoadParser;
        _acceptedParser = acceptedParser;
        _completedParser = completedParser;
        _view = view;
        _refData = refData;
        _time = time ?? TimeProvider.System;
        _diag = diag;

        // Hydrate from disk for the active character (if any) before any
        // subscriber attaches. CurrentChanged covers later character switches
        // and the case where no character is selected yet at construction.
        ReloadFromView();
        _view.CurrentChanged += OnViewCurrentChanged;
    }

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
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            foreach (var (_, entry) in _active)
                Invoke(handler, new QuestEvent(QuestEventKind.Accepted, entry.InternalName, entry.AcceptedAt.UtcDateTime));
            foreach (var (_, c) in _completed)
                Invoke(handler, new QuestEvent(QuestEventKind.Completed, c.InternalName, c.LastCompletedAt.UtcDateTime));
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("Quests", "Subscribing to L1 driver (LocalPlayer pipe) for quest events");
        // archetype-A defaults — FromSessionStart replay + Inline delivery.
        // Driver-owned containment retires the previous catch + _diag.Warn
        // around Dispatch (capability C of #550). Each parser consumes the
        // envelope-stripped LocalPlayerLogLine.Data directly — L0.5 (#532)
        // owns actor classification (#550 PR #555 review).
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
    /// <see cref="QuestEventKind.Accepted"/> for newly-present entries and
    /// <see cref="QuestEventKind.Abandoned"/> for entries that were active
    /// before but aren't anymore.
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

        var events = new List<QuestEvent>();
        bool mutated = false;
        lock (_lock)
        {
            foreach (var (name, _) in _active)
                if (!newActive.ContainsKey(name))
                    events.Add(new QuestEvent(QuestEventKind.Abandoned, name, loaded.Timestamp));
            foreach (var (name, entry) in newActive)
                if (!_active.ContainsKey(name))
                    events.Add(new QuestEvent(QuestEventKind.Accepted, name, entry.AcceptedAt.UtcDateTime));

            if (events.Count > 0)
            {
                _active.Clear();
                foreach (var (name, entry) in newActive) _active[name] = entry;
                mutated = true;
                FireAll(events);
            }
        }
        if (mutated) PersistAfterMutation();
    }

    private void HandleAccepted(QuestAcceptedEvent ev)
    {
        if (string.IsNullOrEmpty(ev.QuestInternalName)) return;
        var stamp = new DateTimeOffset(ev.Timestamp, TimeSpan.Zero);
        bool mutated = false;
        lock (_lock)
        {
            if (!_active.ContainsKey(ev.QuestInternalName))
            {
                _active[ev.QuestInternalName] = new QuestJournalEntry(ev.QuestInternalName, stamp);
                FireOne(new QuestEvent(QuestEventKind.Accepted, ev.QuestInternalName, ev.Timestamp));
                mutated = true;
            }
        }
        if (mutated) PersistAfterMutation();
    }

    private void HandleCompleted(QuestCompletedEvent ev)
    {
        if (string.IsNullOrEmpty(ev.QuestInternalName)) return;
        var stamp = new DateTimeOffset(ev.Timestamp, TimeSpan.Zero);
        bool persist = false;
        lock (_lock)
        {
            // Idempotency: a replayed ProcessCompleteQuest line carries the
            // same Timestamp. Don't re-fire Completed (downstream cooldown
            // anchors are past-anchored and the duplicate would no-op anyway,
            // but more importantly we don't want subscribers to think a fresh
            // turn-in just happened).
            bool isNew = !_completed.TryGetValue(ev.QuestInternalName, out var prior)
                         || prior.LastCompletedAt != stamp;
            bool wasActive = _active.Remove(ev.QuestInternalName);

            if (isNew)
            {
                _completed[ev.QuestInternalName] = new QuestCompletionState(ev.QuestInternalName, stamp);
                FireOne(new QuestEvent(QuestEventKind.Completed, ev.QuestInternalName, ev.Timestamp));
                persist = true;
            }
            else if (wasActive)
            {
                // Replayed completion + quest still in active set means the
                // stored state never recorded the original removal. Reconcile
                // silently (no Abandoned event — would mislead subscribers
                // into thinking a fresh abandon happened).
                persist = true;
            }
        }
        if (persist) PersistAfterMutation();
    }

    private void OnViewCurrentChanged(object? sender, EventArgs e)
    {
        try { ReloadFromView(); }
        catch (Exception ex) { _diag?.Warn("Quests", $"Character switch reload failed: {ex.Message}"); }
    }

    /// <summary>
    /// Atomically swap the in-memory state to whatever
    /// <see cref="PerCharacterView{T}.Current"/> reports, firing diff events
    /// (<see cref="QuestEventKind.Abandoned"/> for old-active-only,
    /// <see cref="QuestEventKind.Accepted"/> for new-active-only,
    /// <see cref="QuestEventKind.Completed"/> for new/changed completion
    /// entries) so subscribers can update mirrors without re-subscribing.
    /// </summary>
    private void ReloadFromView()
    {
        var current = _view.Current;
        var newActive = current?.ActiveQuests ?? new Dictionary<string, QuestJournalEntry>(StringComparer.OrdinalIgnoreCase);
        var newCompleted = current?.CompletionHistory ?? new Dictionary<string, QuestCompletionState>(StringComparer.OrdinalIgnoreCase);

        var events = new List<QuestEvent>();
        var nowStamp = _time.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            foreach (var (name, _) in _active)
                if (!newActive.ContainsKey(name))
                    events.Add(new QuestEvent(QuestEventKind.Abandoned, name, nowStamp));
            foreach (var (name, entry) in newActive)
                if (!_active.ContainsKey(name))
                    events.Add(new QuestEvent(QuestEventKind.Accepted, name, entry.AcceptedAt.UtcDateTime));
            foreach (var (name, c) in newCompleted)
                if (!_completed.TryGetValue(name, out var oc) || oc.LastCompletedAt != c.LastCompletedAt)
                    events.Add(new QuestEvent(QuestEventKind.Completed, name, c.LastCompletedAt.UtcDateTime));

            _active.Clear();
            foreach (var (k, v) in newActive) _active[k] = v;
            _completed.Clear();
            foreach (var (k, v) in newCompleted) _completed[k] = v;

            if (events.Count > 0) FireAll(events);
        }
    }

    private void PersistAfterMutation()
    {
        var current = _view.Current;
        if (current is null) return;

        Dictionary<string, QuestJournalEntry> activeSnap;
        Dictionary<string, QuestCompletionState> completedSnap;
        lock (_lock)
        {
            activeSnap = new Dictionary<string, QuestJournalEntry>(_active, StringComparer.OrdinalIgnoreCase);
            completedSnap = new Dictionary<string, QuestCompletionState>(_completed, StringComparer.OrdinalIgnoreCase);
        }

        current.ActiveQuests = activeSnap;
        current.CompletionHistory = completedSnap;
        try { _view.Save(); }
        catch (Exception ex) { _diag?.Warn("Quests", $"Save failed: {ex.Message}"); }
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void FireOne(QuestEvent ev)
    {
        foreach (var h in _handlers) Invoke(h, ev);
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void FireAll(IReadOnlyList<QuestEvent> events)
    {
        foreach (var ev in events)
            foreach (var h in _handlers)
                Invoke(h, ev);
    }

    private void Invoke(Action<QuestEvent> handler, QuestEvent ev)
    {
        try { handler(ev); }
        catch (Exception ex) { _diag?.Warn("Quests", $"Subscriber threw: {ex.Message}"); }
    }

    public override void Dispose()
    {
        _view.CurrentChanged -= OnViewCurrentChanged;
        _subscription?.Dispose();
        _subscription = null;
        base.Dispose();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly QuestService _svc;
        private Action<QuestEvent>? _handler;
        public Subscription(QuestService svc, Action<QuestEvent> handler) { _svc = svc; _handler = handler; }
        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handler, null);
            if (h is null) return;
            lock (_svc._lock) _svc._handlers.Remove(h);
        }
    }
}
