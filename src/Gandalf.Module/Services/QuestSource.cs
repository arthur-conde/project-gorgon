using System.Diagnostics.CodeAnalysis;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using Gandalf.Domain;
using Mithril.Reference.Models.Quests;
using Mithril.Shared.Reference;

namespace Gandalf.Services;

/// <summary>
/// <see cref="ITimerSource"/> for repeatable-quest cooldowns. Pure projector
/// over <see cref="IQuestState"/> (Arda L3) + <see cref="DerivedTimerProgressService"/>:
/// catalog enumerates active quests (keyed by quest ID, resolved to InternalName
/// via reference data) plus keys with existing progress. Cooldown anchors on
/// <see cref="QuestCompleted"/> domain events.
/// </summary>
public sealed class QuestSource : ITimerSource, IDisposable
{
    public const string Id = "gandalf.quest";

    private readonly DerivedTimerProgressService _derived;
    private readonly IReferenceDataService _refData;
    private readonly IQuestState _questState;
    private readonly TimeProvider _time;
    private readonly ICalendarState? _calendarState;
    private readonly object _lock = new();
    private readonly IDisposable _completedSub;
    private readonly IDisposable _loadedSub;
    private readonly IDisposable _acceptedSub;
    private IReadOnlyList<TimerCatalogEntry> _catalog;
    private IReadOnlyDictionary<string, TimerCatalogEntry> _lastCatalogByKey;
    private IReadOnlyDictionary<string, TimerProgressEntry> _lastProgressByKey;

    public QuestSource(
        DerivedTimerProgressService derived,
        IReferenceDataService refData,
        IQuestState questState,
        IDomainEventSubscriber domainBus,
        TimeProvider? time = null,
        ICalendarState? calendarState = null)
    {
        _derived = derived;
        _refData = refData;
        _questState = questState;
        _time = time ?? TimeProvider.System;
        _calendarState = calendarState;
        _catalog = BuildCatalog();
        _lastCatalogByKey = _catalog.ToDictionary(c => c.Key, StringComparer.Ordinal);
        _lastProgressByKey = SnapshotProgress();

        _derived.ProgressChanged += OnDerivedProgressChanged;
        _refData.FileUpdated += OnReferenceFileUpdated;
        _completedSub = domainBus.Subscribe<QuestCompleted>(OnQuestCompleted);
        _loadedSub = domainBus.Subscribe<QuestsLoaded>(_ => RebuildCatalogAndEmit());
        _acceptedSub = domainBus.Subscribe<QuestAccepted>(_ => RebuildCatalogAndEmit());
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

    private void OnQuestCompleted(QuestCompleted evt)
    {
        var internalName = ResolveInternalName(evt.QuestId);
        if (internalName is null) return;

        var ts = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        AnchorCompletionCooldown(internalName, ts.UtcDateTime);
    }

    /// <summary>
    /// Apply a quest-completion observation: stamp the cooldown row anchored
    /// on the log-line timestamp.
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
        if (prior is not null && prior.StartedAt == startedAt) return;

        _derived.Start(Id, key, startedAt);

        var readyAt = startedAt + duration;
        if (readyAt <= (_calendarState?.LastTimestamp ?? _time.GetUtcNow()))
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

        RebuildCatalogAndEmit();
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
    /// Project the rendered universe: every quest in the active journal
    /// resolved to InternalName via reference data, plus every key with
    /// non-null cooldown progress.
    /// </summary>
    private IReadOnlyList<TimerCatalogEntry> BuildCatalog()
    {
        var list = new List<TimerCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (questId, _) in _questState.ActiveQuests)
        {
            var internalName = ResolveInternalName(questId);
            if (internalName is null) continue;
            if (!_refData.QuestsByInternalName.TryGetValue(internalName, out var quest)) continue;
            var duration = ComputeDuration(quest);
            if (duration <= TimeSpan.Zero) continue;

            var entry = ProjectEntry(quest, duration);
            list.Add(entry);
            seen.Add(entry.Key);
        }

        foreach (var (key, progress) in _derived.SnapshotFor(Id))
        {
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

    private string? ResolveInternalName(int questId) =>
        _refData.Quests.TryGetValue($"quest_{questId}", out var entry) ? entry.InternalName : null;

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
        _completedSub.Dispose();
        _loadedSub.Dispose();
        _acceptedSub.Dispose();
        _derived.ProgressChanged -= OnDerivedProgressChanged;
        _refData.FileUpdated -= OnReferenceFileUpdated;
    }

    private static readonly IReadOnlyDictionary<string, TimerProgressEntry> EmptyProgress =
        new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal);
}
