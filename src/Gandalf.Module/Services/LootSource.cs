using Gandalf.Domain;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Settings;

namespace Gandalf.Services;

/// <summary>
/// Cross-source <see cref="ITimerSource"/> for static chest + reward-cooldown
/// defeat timers. Chest catalog is observed (durations cached as the player
/// learns them from rejection screen text); defeat catalog is bundled
/// + overlaid from <c>mithril-calibration/defeats.json</c> when shipped.
///
/// Verification owed: v1 keys on chest internal name only (no area). The wiki
/// caveat under "Static treasure chests § Per-instance vs per-template state"
/// notes two GoblinStaticChest1 spawns in one zone may share state; refine when
/// the parser spike captures area-disambiguating signals.
/// </summary>
public sealed class LootSource : ITimerSource, IDisposable
{
    public const string Id = "gandalf.loot";

    private readonly DerivedTimerProgressService _derived;
    private readonly ISettingsStore<LootCatalogCache> _cacheStore;
    private readonly LootCatalogCache _cache;
    private readonly TimeProvider _time;
    private readonly IDiagnosticsSink? _diag;
    private readonly object _catalogLock = new();
    private IReadOnlyList<DefeatCatalogEntry> _defeatCatalog;
    private IReadOnlyList<TimerCatalogEntry> _catalog;

    public LootSource(
        DerivedTimerProgressService derived,
        ISettingsStore<LootCatalogCache> cacheStore,
        LootCatalogCache cache,
        IEnumerable<DefeatCatalogEntry>? defeats = null,
        TimeProvider? time = null,
        IDiagnosticsSink? diag = null)
    {
        _derived = derived;
        _cacheStore = cacheStore;
        _cache = cache;
        _time = time ?? TimeProvider.System;
        _diag = diag;
        _defeatCatalog = (defeats ?? []).ToArray();
        _catalog = BuildCatalog();

        _derived.ProgressChanged += OnDerivedProgressChanged;
    }

    public string SourceId => Id;
    public IReadOnlyList<TimerCatalogEntry> Catalog => _catalog;
    public IReadOnlyDictionary<string, TimerProgressEntry> Progress => SnapshotProgress();

    public event EventHandler? CatalogChanged;
    public event EventHandler? ProgressChanged;
    public event EventHandler<TimerReadyEventArgs>? TimerReady;

    /// <summary>
    /// Apply a chest interaction observation: stamp a cooldown row anchored on
    /// the log timestamp. If the duration for this chest template is unknown
    /// the row is skipped (we'll learn the duration on a future re-loot
    /// rejection and backfill on the next interaction).
    /// </summary>
    public void OnChestInteraction(string chestInternalName, DateTime timestampUtc)
    {
        if (string.IsNullOrEmpty(chestInternalName)) return;
        if (!_cache.ChestDurationByInternalName.TryGetValue(chestInternalName, out var duration))
        {
            // First-ever loot of this chest template — duration is unknown until
            // a future re-loot rejection populates the catalog. Don't create a
            // row with a guessed duration.
            return;
        }

        var key = ChestKey(chestInternalName);
        var prior = _derived.GetProgress(Id, key);
        var startedAt = new DateTimeOffset(timestampUtc, TimeSpan.Zero);

        // Idempotency: if we already track this chest with the same StartedAt,
        // skip the redundant write so we don't churn the persistence layer on
        // log replay.
        if (prior is not null && prior.StartedAt == startedAt && prior.DismissedAt is null) return;

        _derived.Start(Id, key, startedAt);
        EnsureCatalogContainsChest(chestInternalName, duration);
        FireReady(key, chestInternalName, durationOverride: duration, atUtc: startedAt + duration);
    }

    /// <summary>
    /// Apply a chest rejection observation: cache the discovered duration
    /// against the chest template name. Future first-loots of any chest of this
    /// template will start with the right duration.
    /// </summary>
    public void OnChestCooldownObserved(string chestInternalName, TimeSpan duration)
    {
        if (string.IsNullOrEmpty(chestInternalName) || duration <= TimeSpan.Zero) return;
        var changed = false;
        lock (_catalogLock)
        {
            if (!_cache.ChestDurationByInternalName.TryGetValue(chestInternalName, out var existing)
                || existing != duration)
            {
                _cache.ChestDurationByInternalName[chestInternalName] = duration;
                changed = true;
            }
        }
        if (changed)
        {
            try { _cacheStore.Save(_cache); } catch { /* best-effort */ }
            EnsureCatalogContainsChest(chestInternalName, duration);
        }
    }

    /// <summary>
    /// Apply a scripted-event-class kill-credit observation (Olugax and similar).
    /// The game emits the kill credit on every kill regardless of cooldown, so
    /// within-window re-kills must be suppressed locally to avoid resetting the
    /// clock. Routes only entries whose <see cref="DefeatCatalogEntry.Class"/>
    /// is <see cref="DefeatClass.ScriptedEvent"/>.
    /// </summary>
    public void OnScriptedEventBossDefeated(string npcDisplayName, DateTime timestampUtc)
    {
        var entry = LookupByDisplayName(npcDisplayName);
        if (entry is null || entry.Class != DefeatClass.ScriptedEvent) return;

        var key = DefeatKey(entry.NpcInternalName);
        var prior = _derived.GetProgress(Id, key);
        var startedAt = new DateTimeOffset(timestampUtc, TimeSpan.Zero);

        // The kill-credit line fires on every kill regardless of cooldown state
        // (per the wiki — the "reduced rewards" discriminator is still
        // unidentified). Suppress repeats while the cooldown is still active so
        // a within-window kill doesn't reset the clock locally.
        if (prior is not null
            && prior.DismissedAt is null
            && _time.GetUtcNow() < prior.StartedAt + entry.RewardCooldown)
        {
            return;
        }

        _derived.Start(Id, key, startedAt);
        FireReady(key, entry.DisplayName, durationOverride: entry.RewardCooldown, atUtc: startedAt + entry.RewardCooldown);
    }

    /// <summary>
    /// Apply a defeat-cooldown-class corpse-search observation (Megaspider and
    /// similar). The corpse-search line fires for every mob the player kills,
    /// so the catalog filter by display name + <see cref="DefeatClass.DefeatCooldown"/>
    /// is what restricts this to actual cooldown bosses. No local suppression
    /// — the game already gates re-kills server-side, so a positive signal
    /// always means a real kill.
    /// </summary>
    public void OnDefeatCooldownObserved(string npcDisplayName, DateTime timestampUtc)
    {
        var entry = LookupByDisplayName(npcDisplayName);
        if (entry is null || entry.Class != DefeatClass.DefeatCooldown) return;

        var key = DefeatKey(entry.NpcInternalName);
        var prior = _derived.GetProgress(Id, key);
        var startedAt = new DateTimeOffset(timestampUtc, TimeSpan.Zero);

        // Idempotency: identical timestamp + still-active row → no churn.
        if (prior is not null && prior.StartedAt == startedAt && prior.DismissedAt is null) return;

        _derived.Start(Id, key, startedAt);
        FireReady(key, entry.DisplayName, durationOverride: entry.RewardCooldown, atUtc: startedAt + entry.RewardCooldown);
    }

    /// <summary>
    /// Apply a "you have already killed &lt;X&gt; too recently" rejection observation.
    /// Both class types can emit this — Olugax (scripted-event) was confirmed in
    /// captures alongside Megaspider (defeat-cooldown). v1 is diagnostic-only:
    /// the prior kill that started the cooldown already stamped a row via the
    /// positive path. If no row exists (e.g. Mithril started mid-cooldown), the
    /// row will appear on the next successful kill.
    /// </summary>
    public void OnDefeatCooldownActive(string npcDisplayName, DateTime timestampUtc)
    {
        var entry = LookupByDisplayName(npcDisplayName);
        if (entry is null) return;
        _diag?.Trace("Gandalf.Loot",
            $"Cooldown still active for {entry.DisplayName} at {timestampUtc:O}");
    }

    private DefeatCatalogEntry? LookupByDisplayName(string npcDisplayName)
    {
        if (string.IsNullOrEmpty(npcDisplayName)) return null;
        return _defeatCatalog.FirstOrDefault(d =>
            string.Equals(d.DisplayName, npcDisplayName, StringComparison.OrdinalIgnoreCase));
    }

    public void OverlayDefeatCatalog(IEnumerable<DefeatCatalogEntry> entries)
    {
        lock (_catalogLock)
        {
            _defeatCatalog = entries.ToArray();
            _catalog = BuildCatalog();
        }
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDerivedProgressChanged(object? sender, EventArgs e) =>
        ProgressChanged?.Invoke(this, EventArgs.Empty);

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
        var list = new List<TimerCatalogEntry>(_cache.ChestDurationByInternalName.Count + _defeatCatalog.Count);
        foreach (var (internalName, duration) in _cache.ChestDurationByInternalName)
        {
            list.Add(new TimerCatalogEntry(
                Key: ChestKey(internalName),
                DisplayName: internalName,
                Region: "Chests",
                Duration: duration,
                SourceMetadata: new LootCatalogPayload(LootKind.Chest, internalName, Region: null)));
        }
        foreach (var d in _defeatCatalog)
        {
            list.Add(new TimerCatalogEntry(
                Key: DefeatKey(d.NpcInternalName),
                DisplayName: d.DisplayName,
                Region: string.IsNullOrEmpty(d.Area) ? "Defeats" : d.Area,
                Duration: d.RewardCooldown,
                SourceMetadata: new LootCatalogPayload(LootKind.Defeat, d.NpcInternalName, d.Area)));
        }
        return list;
    }

    private void EnsureCatalogContainsChest(string internalName, TimeSpan duration)
    {
        var raised = false;
        lock (_catalogLock)
        {
            // Reproject — duration may have changed for an already-known chest, or
            // the catalog may not yet include this internalName.
            var newCatalog = BuildCatalog();
            if (!ReferenceEquals(_catalog, newCatalog))
            {
                _catalog = newCatalog;
                raised = true;
            }
        }
        if (raised) CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FireReady(string key, string displayName, TimeSpan durationOverride, DateTimeOffset atUtc)
    {
        // Fire eagerly when stamping past-anchored: the row may already be ready.
        if (atUtc <= _time.GetUtcNow())
        {
            TimerReady?.Invoke(this, new TimerReadyEventArgs
            {
                SourceId = Id,
                Key = key,
                DisplayName = displayName,
                ReadyAt = atUtc,
                SourceMetadata = null,
            });
        }
    }

    public static string ChestKey(string internalName) => $"chest:{internalName}";
    public static string DefeatKey(string npcInternalName) => $"defeat:{npcInternalName}";

    public void Dispose()
    {
        _derived.ProgressChanged -= OnDerivedProgressChanged;
    }

    private static readonly IReadOnlyDictionary<string, TimerProgressEntry> EmptyProgress =
        new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal);
}
