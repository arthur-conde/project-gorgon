using Gandalf.Domain;
using Mithril.Shared.Reference;

namespace Gandalf.Services;

/// <summary>
/// <see cref="ITimerSource"/> for repeatable-quest cooldowns. Catalog projects
/// from <see cref="IReferenceDataService.Quests"/> filtered to entries with a
/// <c>Reuse*</c> duration and no time-flavored requirement gate. Progress
/// routes through <see cref="DerivedTimerProgressService"/> with sourceId
/// <c>"gandalf.quest"</c>.
///
/// "Pending" filter: tracks an in-memory set of currently-loaded quests built
/// from log replay each session. <see cref="QuestCompletedEvent"/> drops the
/// quest from Pending and starts the cooldown; <see cref="QuestLoadedEvent"/>
/// adds it to Pending.
/// </summary>
public sealed class QuestSource : ITimerSource, IDisposable
{
    public const string Id = "gandalf.quest";

    private static readonly HashSet<string> TimeGatedRequirementTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "QuestCompletedRecently",
    };
    private static readonly string TimeGatedPrefix = "MinDelayAfterFirstCompletion";

    private readonly DerivedTimerProgressService _derived;
    private readonly IReferenceDataService _refData;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private IReadOnlyList<TimerCatalogEntry> _catalog;
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    public QuestSource(
        DerivedTimerProgressService derived,
        IReferenceDataService refData,
        TimeProvider? time = null)
    {
        _derived = derived;
        _refData = refData;
        _time = time ?? TimeProvider.System;
        _catalog = BuildCatalog();

        _derived.ProgressChanged += OnDerivedProgressChanged;
        _refData.FileUpdated += OnReferenceFileUpdated;
    }

    public string SourceId => Id;
    public IReadOnlyList<TimerCatalogEntry> Catalog => _catalog;
    public IReadOnlyDictionary<string, TimerProgressEntry> Progress => SnapshotProgress();

    public event EventHandler? CatalogChanged;
    public event EventHandler? ProgressChanged;
    public event EventHandler<TimerReadyEventArgs>? TimerReady;

    /// <summary>Snapshot of quests currently loaded in the player's journal (for the Pending filter).</summary>
    public IReadOnlySet<string> PendingInternalNames
    {
        get { lock (_lock) return new HashSet<string>(_pending, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// Apply a quest-loaded observation: add the InternalName to the in-memory
    /// pending set so the UI can filter for "in journal, not completed".
    /// </summary>
    public void OnQuestLoaded(string questInternalName)
    {
        if (string.IsNullOrEmpty(questInternalName)) return;
        bool changed;
        lock (_lock) changed = _pending.Add(questInternalName);
        if (changed) ProgressChanged?.Invoke(this, EventArgs.Empty);
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
        if (prior is not null && prior.StartedAt == startedAt && prior.DismissedAt is null) return;

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

    private void OnDerivedProgressChanged(object? sender, EventArgs e) =>
        ProgressChanged?.Invoke(this, EventArgs.Empty);

    private void OnReferenceFileUpdated(object? sender, string fileKey)
    {
        if (!string.Equals(fileKey, "quests", StringComparison.OrdinalIgnoreCase)) return;
        var newCatalog = BuildCatalog();
        lock (_lock) _catalog = newCatalog;

        // GC orphaned progress entries — quests removed from the catalog (or
        // newly time-gated) shouldn't keep stale rows alive.
        var validKeys = newCatalog.Select(c => c.Key).ToArray();
        _derived.GarbageCollect(Id, validKeys);

        CatalogChanged?.Invoke(this, EventArgs.Empty);
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
            if (!IsRepeatableNonTimeGated(quest)) continue;
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

    private static bool IsRepeatableNonTimeGated(QuestEntry quest)
    {
        if (HasReuse(quest) is false) return false;

        // Drop quests gated by non-Reuse cooldowns — every cooldown shown must be
        // one this source can compute correctly.
        foreach (var req in quest.Requirements)
        {
            if (req.Type is null) continue;
            if (TimeGatedRequirementTypes.Contains(req.Type)) return false;
            if (req.Type.StartsWith(TimeGatedPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool HasReuse(QuestEntry q) =>
        (q.ReuseMinutes ?? 0) > 0 || (q.ReuseHours ?? 0) > 0 || (q.ReuseDays ?? 0) > 0;

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
