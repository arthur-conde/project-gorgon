using System.Diagnostics.CodeAnalysis;
using Gandalf.Domain;
using Mithril.GameState.Quests;
using Mithril.Reference.Models.Quests;
using Mithril.Shared.Reference;

namespace Gandalf.Services;

/// <summary>
/// <see cref="ITimerSource"/> for repeatable-quest cooldowns. Pure projector
/// over <see cref="IPlayerQuestJournalService"/> + <see cref="DerivedTimerProgressService"/>:
/// catalog enumerates <c>ActiveQuests ∪ keys-with-progress</c> joined against
/// <see cref="IReferenceDataService.QuestsByInternalName"/> for static fields.
/// Active set comes from <see cref="IPlayerQuestJournalService.ActiveQuests"/> (per-character,
/// persisted in Mithril.GameState); cooldown progress stays Gandalf-internal
/// via <see cref="DerivedTimerProgressService"/>.
///
/// On <see cref="QuestEventKind.Completed"/> the source anchors the cooldown
/// row past-anchored on the log-line timestamp, mirroring what the old inline
/// <c>OnQuestCompleted</c> handler did before #155 split ingestion out.
///
/// Eligibility gates (<c>QuestCompletedRecently</c>, <c>MinFavorLevel</c>,
/// <c>MinSkillLevel</c>, …) are intentionally not re-evaluated here. The
/// game is the authoritative gate: a <see cref="QuestEventKind.Completed"/>
/// observation already implies the server validated every requirement.
/// </summary>
public sealed class QuestSource : ITimerSource, IDisposable
{
    public const string Id = "gandalf.quest";

    private readonly DerivedTimerProgressService _derived;
    private readonly IReferenceDataService _refData;
    private readonly IPlayerQuestJournalService _questSvc;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly IDisposable _questSubscription;
    private IReadOnlyList<TimerCatalogEntry> _catalog;
    private IReadOnlyDictionary<string, TimerCatalogEntry> _lastCatalogByKey;
    private IReadOnlyDictionary<string, TimerProgressEntry> _lastProgressByKey;

    public QuestSource(
        DerivedTimerProgressService derived,
        IReferenceDataService refData,
        IPlayerQuestJournalService questSvc,
        TimeProvider? time = null)
    {
        _derived = derived;
        _refData = refData;
        _questSvc = questSvc;
        _time = time ?? TimeProvider.System;
        _catalog = BuildCatalog();
        _lastCatalogByKey = _catalog.ToDictionary(c => c.Key, StringComparer.Ordinal);
        _lastProgressByKey = SnapshotProgress();

        _derived.ProgressChanged += OnDerivedProgressChanged;
        _refData.FileUpdated += OnReferenceFileUpdated;
        // Subscribe last so all our fields are initialised — Subscribe's
        // atomic replay fires synthetic Accepted/Completed events synchronously
        // on the calling thread, which immediately re-enters our handler.
        _questSubscription = _questSvc.Subscribe(OnQuestEvent);
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

    public event EventHandler<TimerReadyEventArgs>? TimerReady;
    public event EventHandler<TimerRowsChangedEventArgs>? RowsChanged;

    private void OnQuestEvent(QuestEvent ev)
    {
        switch (ev.Kind)
        {
            case QuestEventKind.Completed:
                AnchorCompletionCooldown(ev.InternalName, ev.Timestamp);
                break;
            case QuestEventKind.Accepted:
            case QuestEventKind.Abandoned:
                RebuildCatalogAndEmit();
                break;
        }
    }

    /// <summary>
    /// Apply a quest-completion observation: stamp the cooldown row anchored
    /// on the log-line timestamp. Skips quests with no <c>Reuse*</c> duration
    /// (orphan completion — rare but possible if reference data dropped a
    /// quest the user has in their journal).
    /// </summary>
    private void AnchorCompletionCooldown(string questInternalName, DateTime timestampUtc)
    {
        if (string.IsNullOrEmpty(questInternalName)) return;
        if (!_refData.QuestsByInternalName.TryGetValue(questInternalName, out var quest)) return;
        var duration = ComputeDuration(quest);
        if (duration <= TimeSpan.Zero) return;

        var key = QuestKey(questInternalName);
        var startedAt = new DateTimeOffset(timestampUtc, TimeSpan.Zero);
        var prior = _derived.GetProgress(Id, key);
        // Idempotency: matching StartedAt means this is a replay of an
        // already-recorded completion (Subscribe replay or duplicate
        // ProcessCompleteQuest). Skip _derived.Start regardless of
        // DismissedAt — clearing it would silently resurrect a row the user
        // X'd out (bug 1f164cf).
        if (prior is not null && prior.StartedAt == startedAt) return;

        _derived.Start(Id, key, startedAt);
        // OnDerivedProgressChanged will pick up the catalog rebuild + EmitDeltas.

        var readyAt = startedAt + duration;
        if (readyAt <= _time.GetUtcNow())
        {
            TimerReady?.Invoke(this, new TimerReadyEventArgs
            {
                SourceId = Id,
                Key = key,
                DisplayName = quest.Name ?? questInternalName,
                ReadyAt = readyAt,
                SourceMetadata = new QuestCatalogPayload(quest),
            });
        }
    }

    private void RebuildCatalogAndEmit()
    {
        var newCatalog = BuildCatalog();
        lock (_lock) _catalog = newCatalog;
        EmitDeltas();
    }

    private void OnDerivedProgressChanged(object? sender, EventArgs e) => RebuildCatalogAndEmit();

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
    }

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

    /// <summary>
    /// Project the rendered universe: every quest currently in
    /// <see cref="IPlayerQuestJournalService.ActiveQuests"/> joined with reference data,
    /// PLUS every key with non-null cooldown progress (so a completed-but-
    /// still-cooling quest stays visible after it leaves the active journal).
    /// Quests with no <c>Reuse*</c> duration are filtered out — nothing to
    /// render as a timer row.
    /// </summary>
    private IReadOnlyList<TimerCatalogEntry> BuildCatalog()
    {
        var list = new List<TimerCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (internalName, _) in _questSvc.ActiveQuests)
        {
            if (!_refData.QuestsByInternalName.TryGetValue(internalName, out var quest)) continue;
            var duration = ComputeDuration(quest);
            if (duration <= TimeSpan.Zero) continue;

            var entry = ProjectEntry(quest, duration);
            list.Add(entry);
            seen.Add(entry.Key);
        }

        foreach (var (key, progress) in _derived.SnapshotFor(Id))
        {
            // Dismissed progress = "user is done with this row, hide it" — same
            // contract the old IsRelevant predicate enforced before #155
            // collapsed catalog filtering into the projection.
            if (progress.DismissedAt is not null) continue;
            if (!seen.Add(key)) continue;
            var internalName = TryParseInternalName(key);
            if (internalName is null) continue;
            if (!_refData.QuestsByInternalName.TryGetValue(internalName, out var quest)) continue;
            var duration = ComputeDuration(quest);
            if (duration <= TimeSpan.Zero) continue;
            list.Add(ProjectEntry(quest, duration));
        }

        return list;
    }

    private static TimerCatalogEntry ProjectEntry(Quest quest, TimeSpan duration) =>
        new(
            Key: QuestKey(quest.InternalName ?? ""),
            DisplayName: quest.Name ?? quest.InternalName ?? "",
            Region: quest.DisplayedLocation ?? quest.FavorNpc ?? "Quests",
            Duration: duration,
            SourceMetadata: new QuestCatalogPayload(quest));

    private static string? TryParseInternalName(string key) =>
        key.StartsWith("quest:", StringComparison.Ordinal)
            ? key.Substring("quest:".Length)
            : null;

    private static TimeSpan ComputeDuration(Quest q)
    {
        var total = TimeSpan.Zero;
        if (q.ReuseTime_Days is { } d) total += TimeSpan.FromDays(d);
        if (q.ReuseTime_Hours is { } h) total += TimeSpan.FromHours(h);
        if (q.ReuseTime_Minutes is { } m) total += TimeSpan.FromMinutes(m);
        return total;
    }

    public static string QuestKey(string questInternalName) => $"quest:{questInternalName}";

    public void Dispose()
    {
        _questSubscription.Dispose();
        _derived.ProgressChanged -= OnDerivedProgressChanged;
        _refData.FileUpdated -= OnReferenceFileUpdated;
    }

    private static readonly IReadOnlyDictionary<string, TimerProgressEntry> EmptyProgress =
        new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal);
}
