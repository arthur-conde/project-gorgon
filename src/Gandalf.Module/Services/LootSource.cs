using Gandalf.Domain;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Settings;

namespace Gandalf.Services;

/// <summary>
/// Cross-source <see cref="ITimerSource"/> for static chest + defeat-cooldown
/// boss timers. Both surfaces are observation-driven:
///
/// <list type="bullet">
///   <item>Chest durations are learned from the game's "loot N hours from now"
///   re-loot rejection screen text and persisted by chest internal name.</item>
///   <item>Defeat bosses are auto-discovered from the CombatInfo wisdom-credit
///   line (<see cref="OnBossKillCredit"/>) and persisted by display name.
///   Cooldown durations come from the community calibration overlay
///   (<see cref="OverlayDefeatCalibration"/>); not-yet-calibrated bosses get
///   a folklore-default placeholder and surface as
///   <c>IsDurationVerified=false</c> so the UI can flag them.</item>
/// </list>
///
/// Wiki: https://github.com/arthur-conde/project-gorgon/wiki/Player-Log-Signals#defeat-cooldown-creatures
/// </summary>
public sealed class LootSource : ITimerSource, IDisposable
{
    public const string Id = "gandalf.loot";

    /// <summary>
    /// Folklore-default cooldown for a freshly-discovered boss with no
    /// calibration entry yet. 3 h matches the community convention for the
    /// two prototype bosses (Megaspider, Olugax). Surfaces as
    /// <see cref="LootCatalogPayload.IsDurationVerified"/> = false.
    /// </summary>
    public static readonly TimeSpan PlaceholderDefeatDuration = TimeSpan.FromHours(3);

    private readonly DerivedTimerProgressService _derived;
    private readonly ISettingsStore<LootCatalogCache> _cacheStore;
    private readonly LootCatalogCache _cache;
    private readonly TimeProvider _time;
    private readonly IDiagnosticsSink? _diag;
    private readonly object _catalogLock = new();
    private IReadOnlyDictionary<string, DefeatCatalogEntry> _calibrationByDisplayName =
        new Dictionary<string, DefeatCatalogEntry>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<TimerCatalogEntry> _catalog;

    public LootSource(
        DerivedTimerProgressService derived,
        ISettingsStore<LootCatalogCache> cacheStore,
        LootCatalogCache cache,
        TimeProvider? time = null,
        IDiagnosticsSink? diag = null)
    {
        _derived = derived;
        _cacheStore = cacheStore;
        _cache = cache;
        _time = time ?? TimeProvider.System;
        _diag = diag;
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

        // Idempotency: matching StartedAt means this is a replay of the same
        // line. Skip regardless of DismissedAt — clearing DismissedAt would
        // silently resurrect a row the user explicitly X'd out.
        if (prior is not null && prior.StartedAt == startedAt) return;

        _derived.Start(Id, key, startedAt);
        EnsureCatalogReprojected();
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
            EnsureCatalogReprojected();
        }
    }

    /// <summary>
    /// Apply a wisdom-credit boss-kill observation: auto-learn the boss into
    /// the persisted catalog (display-name keyed) and stamp the cooldown row.
    /// Combat Wisdom is awarded only for defeat-cooldown creatures, so the
    /// presence of this signal is itself the boss-class proof — no per-boss
    /// catalog curation needed.
    ///
    /// Duration comes from the calibration overlay if available; otherwise
    /// falls back to <see cref="PlaceholderDefeatDuration"/> with the
    /// catalog entry flagged unverified.
    /// </summary>
    public void OnBossKillCredit(string npcDisplayName, DateTime timestampUtc)
    {
        if (string.IsNullOrEmpty(npcDisplayName)) return;

        var displayName = npcDisplayName.Trim();
        var (duration, _) = ResolveDefeatDuration(displayName);
        var startedAt = new DateTimeOffset(timestampUtc, TimeSpan.Zero);
        var key = DefeatKey(displayName);

        // Persist the discovery so the row reappears next session even before
        // the player re-kills the boss.
        var learnedChanged = false;
        lock (_catalogLock)
        {
            if (!_cache.LearnedDefeats.TryGetValue(displayName, out var entry))
            {
                _cache.LearnedDefeats[displayName] = new LearnedDefeat
                {
                    FirstObservedAt = timestampUtc,
                    LastObservedAt = timestampUtc,
                };
                learnedChanged = true;
            }
            else if (timestampUtc > entry.LastObservedAt)
            {
                entry.LastObservedAt = timestampUtc;
                learnedChanged = true;
            }
        }
        if (learnedChanged)
        {
            try { _cacheStore.Save(_cache); } catch { /* best-effort */ }
            EnsureCatalogReprojected();
        }

        // Idempotency: matching StartedAt means this is a replay of the same
        // wisdom-credit line. Skip regardless of DismissedAt — clearing
        // DismissedAt would silently resurrect a row the user X'd out.
        var prior = _derived.GetProgress(Id, key);
        if (prior is not null && prior.StartedAt == startedAt) return;

        _derived.Start(Id, key, startedAt);
        FireReady(key, displayName, durationOverride: duration, atUtc: startedAt + duration);
    }

    /// <summary>
    /// Apply a "you have already killed &lt;X&gt; too recently" rejection observation.
    /// v1 is diagnostic-only: the prior kill that started the cooldown already
    /// stamped a row via the positive (wisdom-credit) path. If no row exists
    /// (e.g. Mithril started mid-cooldown), the row will appear on the next
    /// successful kill.
    /// </summary>
    public void OnDefeatCooldownActive(string npcDisplayName, DateTime timestampUtc)
    {
        if (string.IsNullOrEmpty(npcDisplayName)) return;
        _diag?.Trace("Gandalf.Loot",
            $"Cooldown still active for {npcDisplayName.Trim()} at {timestampUtc:O}");
    }

    /// <summary>
    /// Replace the calibration overlay (durations + region for known bosses).
    /// Intended for the Gandalf calibration bridge that subscribes to
    /// <see cref="Mithril.Shared.Reference.ICommunityCalibrationService"/> and
    /// refreshes when <c>aggregated/gandalf.json</c> updates. Auto-learned
    /// bosses still appear; they just get the placeholder duration until a
    /// calibration entry covers them.
    /// </summary>
    public void OverlayDefeatCalibration(IEnumerable<DefeatCatalogEntry> entries)
    {
        var byName = entries.ToDictionary(e => e.DisplayName, StringComparer.OrdinalIgnoreCase);
        lock (_catalogLock)
        {
            _calibrationByDisplayName = byName;
            _catalog = BuildCatalog();
        }
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    private (TimeSpan duration, bool verified) ResolveDefeatDuration(string displayName)
    {
        if (_calibrationByDisplayName.TryGetValue(displayName, out var cal))
            return (cal.RewardCooldown, true);
        return (PlaceholderDefeatDuration, false);
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
        var list = new List<TimerCatalogEntry>(
            _cache.ChestDurationByInternalName.Count + _cache.LearnedDefeats.Count);

        foreach (var (internalName, duration) in _cache.ChestDurationByInternalName)
        {
            list.Add(new TimerCatalogEntry(
                Key: ChestKey(internalName),
                DisplayName: internalName,
                Region: "Chests",
                Duration: duration,
                SourceMetadata: new LootCatalogPayload(LootKind.Chest, internalName, Region: null)));
        }

        foreach (var displayName in _cache.LearnedDefeats.Keys)
        {
            var (duration, verified) = ResolveDefeatDuration(displayName);
            _calibrationByDisplayName.TryGetValue(displayName, out var cal);
            var area = cal?.Area;

            list.Add(new TimerCatalogEntry(
                Key: DefeatKey(displayName),
                DisplayName: displayName,
                Region: string.IsNullOrEmpty(area) ? "Defeats" : area,
                Duration: duration,
                SourceMetadata: new LootCatalogPayload(
                    LootKind.Defeat, displayName, area, IsDurationVerified: verified)));
        }

        return list;
    }

    private void EnsureCatalogReprojected()
    {
        bool raised;
        lock (_catalogLock)
        {
            _catalog = BuildCatalog();
            raised = true;
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

    /// <summary>
    /// Defeat row key. Display-name keyed (post-article-strip wisdom-line form)
    /// because the wisdom line is the only signal carrying an NPC identifier
    /// for cooldown bosses, and it has no internal-name field.
    /// </summary>
    public static string DefeatKey(string npcDisplayName) => $"defeat:{npcDisplayName}";

    public void Dispose()
    {
        _derived.ProgressChanged -= OnDerivedProgressChanged;
    }

    private static readonly IReadOnlyDictionary<string, TimerProgressEntry> EmptyProgress =
        new Dictionary<string, TimerProgressEntry>(StringComparer.Ordinal);
}
