using System.Diagnostics.CodeAnalysis;
using Gandalf.Domain;
using Mithril.Shared.Reference;

namespace Gandalf.Services;

/// <summary>
/// <see cref="ITimerSource"/> for repeatable-quest cooldowns. Catalog projects
/// from <see cref="IReferenceDataService.Quests"/> filtered to entries with a
/// <c>Reuse*</c> duration. Progress routes through
/// <see cref="DerivedTimerProgressService"/> with sourceId <c>"gandalf.quest"</c>.
///
/// Eligibility gates (<c>QuestCompletedRecently</c>, <c>MinFavorLevel</c>,
/// <c>MinSkillLevel</c>, …) are intentionally not re-evaluated here. The
/// game is the authoritative gate: a <see cref="QuestCompletedEvent"/>
/// observation already implies the server validated every requirement,
/// so the cooldown can be stamped without local pre-checks. See the wiki
/// note "Mithril does not re-evaluate game gates".
///
/// "Pending" filter: tracks an in-memory set of currently-in-journal quests.
/// <see cref="QuestJournalLoadedEvent"/> snapshot-replaces it on login;
/// <see cref="QuestAcceptedEvent"/> incrementally adds; <see cref="QuestCompletedEvent"/>
/// removes and starts the cooldown.
/// </summary>
public sealed class QuestSource : ITimerSource, IDisposable
{
    public const string Id = "gandalf.quest";

    private readonly DerivedTimerProgressService _derived;
    private readonly IReferenceDataService _refData;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private IReadOnlyList<TimerCatalogEntry> _catalog;
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, TimerCatalogEntry> _lastCatalogByKey;
    private IReadOnlyDictionary<string, TimerProgressEntry> _lastProgressByKey;

    public QuestSource(
        DerivedTimerProgressService derived,
        IReferenceDataService refData,
        TimeProvider? time = null)
    {
        _derived = derived;
        _refData = refData;
        _time = time ?? TimeProvider.System;
        _catalog = BuildCatalog();
        _lastCatalogByKey = _catalog.ToDictionary(c => c.Key, StringComparer.Ordinal);
        _lastProgressByKey = SnapshotProgress();

        _derived.ProgressChanged += OnDerivedProgressChanged;
        _refData.FileUpdated += OnReferenceFileUpdated;
    }

    public string SourceId => Id;
    public IReadOnlyList<TimerCatalogEntry> Catalog => _catalog;
    public IReadOnlyDictionary<string, TimerProgressEntry> Progress => SnapshotProgress();

    public bool TryGetProgress(string key, [NotNullWhen(true)] out TimerProgressEntry? progress)
    {
        var p = _derived.GetProgress(Id, key);
        if (p is null) { progress = null; return false; }
        progress = new TimerProgressEntry(key, p.StartedAt, p.DismissedAt);
        return true;
    }

    public event EventHandler? CatalogChanged;
    public event EventHandler? ProgressChanged;
    public event EventHandler<TimerReadyEventArgs>? TimerReady;
    public event EventHandler<TimerRowsChangedEventArgs>? RowsChanged;

    /// <summary>Snapshot of quests currently loaded in the player's journal (for the Pending filter).</summary>
    public IReadOnlySet<string> PendingInternalNames
    {
        get { lock (_lock) return new HashSet<string>(_pending, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// Apply a bulk journal-load observation: snapshot-replace the pending set
    /// with every quest the server reports as currently in the player's journal.
    /// Resolves int ids → InternalNames via reference data; unknown ids are
    /// dropped silently (game-data drift). Drives the "Pending" filter chip on
    /// fresh login.
    /// </summary>
    public void OnQuestJournalLoaded(IReadOnlyList<int> workOrderQuestIds, IReadOnlyList<int> regularQuestIds)
    {
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in workOrderQuestIds)
            if (_refData.Quests.TryGetValue($"quest_{id}", out var q)) resolved.Add(q.InternalName);
        foreach (var id in regularQuestIds)
            if (_refData.Quests.TryGetValue($"quest_{id}", out var q)) resolved.Add(q.InternalName);

        bool changed;
        lock (_lock)
        {
            changed = !_pending.SetEquals(resolved);
            if (changed)
            {
                _pending.Clear();
                foreach (var n in resolved) _pending.Add(n);
            }
        }
        if (changed)
        {
            // Pending-set changes don't mutate catalog or progress, so the
            // RowsChanged delta will be empty — but the VM still needs to
            // re-evaluate the journal-membership filter via the legacy
            // ProgressChanged event until the binder gains a relevance hook.
            EmitDeltas();
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Apply a quest-accepted observation: add the InternalName to the in-memory
    /// pending set so the UI can filter for "in journal, not completed".
    /// </summary>
    public void OnQuestAccepted(string questInternalName)
    {
        if (string.IsNullOrEmpty(questInternalName)) return;
        bool changed;
        lock (_lock) changed = _pending.Add(questInternalName);
        if (changed)
        {
            EmitDeltas();
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Apply a quest-completed observation: stamp the cooldown row anchored on
    /// the log-line timestamp. Drops the quest from Pending. Skips the quest
    /// if it has no Reuse* duration in the reference data (orphan completion
    /// — rare but possible if the catalog filter drops a quest the user has
    /// in their journal).
    /// </summary>
    public void OnQuestCompleted(string questInternalName, DateTime timestampUtc)
    {
        if (string.IsNullOrEmpty(questInternalName)) return;

        if (!_refData.QuestsByInternalName.TryGetValue(questInternalName, out var quest)) return;
        var duration = ComputeDuration(quest);
        if (duration <= TimeSpan.Zero) return;

        lock (_lock) _pending.Remove(questInternalName);

        var key = QuestKey(questInternalName);
        var startedAt = new DateTimeOffset(timestampUtc, TimeSpan.Zero);
        var prior = _derived.GetProgress(Id, key);
        // Idempotency: matching StartedAt means this is a replay of the same
        // ProcessCompleteQuest line. Skip regardless of DismissedAt —
        // clearing DismissedAt would silently resurrect a row the user X'd out.
        if (prior is not null && prior.StartedAt == startedAt) return;

        _derived.Start(Id, key, startedAt);

        var readyAt = startedAt + duration;
        if (readyAt <= _time.GetUtcNow())
        {
            TimerReady?.Invoke(this, new TimerReadyEventArgs
            {
                SourceId = Id,
                Key = key,
                DisplayName = quest.Name,
                ReadyAt = readyAt,
                SourceMetadata = new QuestCatalogPayload(quest),
            });
        }
    }

    private void OnDerivedProgressChanged(object? sender, EventArgs e)
    {
        EmitDeltas();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnReferenceFileUpdated(object? sender, string fileKey)
    {
        if (!string.Equals(fileKey, "quests", StringComparison.OrdinalIgnoreCase)) return;
        var newCatalog = BuildCatalog();
        lock (_lock) _catalog = newCatalog;

        // GC orphaned progress entries — quests removed from the catalog (or
        // newly time-gated) shouldn't keep stale rows alive.
        var validKeys = newCatalog.Select(c => c.Key).ToArray();
        _derived.GarbageCollect(Id, validKeys);

        EmitDeltas();
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Diff the current <c>(catalog, progress)</c> against the last snapshot,
    /// fire <see cref="RowsChanged"/> if there are deltas, and update the
    /// snapshot. Called from every event-firing site so the new per-key feed
    /// stays consistent with the legacy coarse events during the rollout.
    /// </summary>
    private void EmitDeltas()
    {
        var newCatalog = _catalog.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var newProgress = SnapshotProgress();
        var deltas = TimerRowDeltaDiffer.Diff(
            _lastCatalogByKey, newCatalog,
            _lastProgressByKey, newProgress);
        _lastCatalogByKey = newCatalog;
        _lastProgressByKey = newProgress;
        if (deltas.Count > 0)
            RowsChanged?.Invoke(this, new TimerRowsChangedEventArgs { Deltas = deltas });
    }

    private IReadOnlyDictionary<string, TimerProgressEntry> SnapshotProgress()
    {
        var raw = _derived.SnapshotFor(Id);
        if (raw.Count == 0) return EmptyProgress;

        var map = new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal);
        foreach (var (key, p) in raw)
            map[key] = new TimerProgressEntry(key, p.StartedAt, p.DismissedAt);
        return map;
    }

    private IReadOnlyList<TimerCatalogEntry> BuildCatalog()
    {
        var list = new List<TimerCatalogEntry>();
        foreach (var quest in _refData.Quests.Values)
        {
            var duration = ComputeDuration(quest);
            if (duration <= TimeSpan.Zero) continue;

            list.Add(new TimerCatalogEntry(
                Key: QuestKey(quest.InternalName),
                DisplayName: quest.Name,
                Region: quest.DisplayedLocation ?? quest.FavorNpc ?? "Quests",
                Duration: duration,
                SourceMetadata: new QuestCatalogPayload(quest)));
        }
        return list;
    }

    private static TimeSpan ComputeDuration(QuestEntry q)
    {
        var total = TimeSpan.Zero;
        if (q.ReuseDays is { } d) total += TimeSpan.FromDays(d);
        if (q.ReuseHours is { } h) total += TimeSpan.FromHours(h);
        if (q.ReuseMinutes is { } m) total += TimeSpan.FromMinutes(m);
        return total;
    }

    public static string QuestKey(string questInternalName) => $"quest:{questInternalName}";

    public void Dispose()
    {
        _derived.ProgressChanged -= OnDerivedProgressChanged;
        _refData.FileUpdated -= OnReferenceFileUpdated;
    }

    private static readonly IReadOnlyDictionary<string, TimerProgressEntry> EmptyProgress =
        new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal);
}
