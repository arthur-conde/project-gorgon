using System.Diagnostics.CodeAnalysis;
using Gandalf.Domain;
using Mithril.GameState.Areas;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;

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
/// Wiki: https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#defeat-cooldown-creatures
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

    /// <summary>
    /// Folklore-default cooldown for a chest looted before any rejection has
    /// taught us the real duration. The chest's row appears immediately
    /// anchored on the original loot timestamp; on the next rejection the
    /// duration upgrades in place and <see cref="LootCatalogPayload.IsDurationVerified"/>
    /// flips to <c>true</c> without resetting the clock.
    /// </summary>
    public static readonly TimeSpan PlaceholderChestDuration = TimeSpan.FromHours(3);

    private readonly DerivedTimerProgressService _derived;
    private readonly ISettingsStore<LootCatalogCache> _cacheStore;
    private readonly LootCatalogCache _cache;
    private readonly PlayerAreaTracker? _areaTracker;
    private readonly IReferenceDataService? _refData;
    private readonly TimeProvider _time;
    private readonly IWorldClock? _worldClock;
    private readonly IDiagnosticsSink? _diag;
    private readonly object _catalogLock = new();
    private IReadOnlyDictionary<string, DefeatCatalogEntry> _calibrationByDisplayName =
        new Dictionary<string, DefeatCatalogEntry>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<TimerCatalogEntry> _catalog;
    private IReadOnlyDictionary<string, TimerCatalogEntry> _lastCatalogByKey;
    private IReadOnlyDictionary<string, TimerProgressEntry> _lastProgressByKey;

    public LootSource(
        DerivedTimerProgressService derived,
        ISettingsStore<LootCatalogCache> cacheStore,
        LootCatalogCache cache,
        PlayerAreaTracker? areaTracker = null,
        IReferenceDataService? refData = null,
        TimeProvider? time = null,
        IDiagnosticsSink? diag = null,
        IPlayerWorld? playerWorld = null)
    {
        _derived = derived;
        _cacheStore = cacheStore;
        _cache = cache;
        _areaTracker = areaTracker;
        _refData = refData;
        _time = time ?? TimeProvider.System;
        _worldClock = playerWorld?.Clock;
        _diag = diag;
        _catalog = BuildCatalog();
        _lastCatalogByKey = _catalog.ToDictionary(c => c.Key, StringComparer.Ordinal);
        _lastProgressByKey = SnapshotProgress();

        _derived.ProgressChanged += OnDerivedProgressChanged;
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

    /// <summary>
    /// Apply a chest interaction observation: stamp a cooldown row anchored on
    /// the log timestamp. If the chest's real duration hasn't been observed
    /// yet (no rejection screen has fired for this template) the row is
    /// stamped with <see cref="PlaceholderChestDuration"/> and surfaces as
    /// <c>IsDurationVerified=false</c>; on a future rejection
    /// <see cref="OnChestCooldownObserved"/> upgrades the duration without
    /// resetting the row's <c>StartedAt</c>.
    /// </summary>
    public void OnChestInteraction(string chestInternalName, DateTime timestampUtc)
    {
        if (string.IsNullOrEmpty(chestInternalName)) return;

        var (duration, _) = ResolveChestDuration(chestInternalName);
        var key = ChestKey(chestInternalName);
        var prior = _derived.GetProgress(Id, key);
        var startedAt = new DateTimeOffset(timestampUtc, TimeSpan.Zero);

        // Persist the discovery so the row survives across sessions even if
        // the rejection screen never fires (placeholder behaviour mirrors the
        // defeat-cooldown auto-learn path). Area is stamped sticky-once-known
        // (#178) — first commit wins so the same internal name in two zones
        // doesn't ping-pong its area attribution.
        var currentArea = _areaTracker?.CurrentArea;
        var learnedChanged = false;
        lock (_catalogLock)
        {
            if (!_cache.LearnedChests.TryGetValue(chestInternalName, out var entry))
            {
                _cache.LearnedChests[chestInternalName] = new LearnedChest
                {
                    FirstObservedAt = timestampUtc,
                    LastObservedAt = timestampUtc,
                    Area = currentArea,
                };
                learnedChanged = true;
            }
            else
            {
                if (timestampUtc > entry.LastObservedAt)
                {
                    entry.LastObservedAt = timestampUtc;
                    learnedChanged = true;
                }
                if (entry.Area is null && currentArea is not null)
                {
                    entry.Area = currentArea;
                    learnedChanged = true;
                }
            }
        }
        if (learnedChanged)
        {
            try { _cacheStore.Save(_cache); } catch { /* best-effort */ }
            EnsureCatalogReprojected();
        }

        // Idempotency: matching StartedAt means this is a replay of the same
        // line. Skip regardless of DismissedAt — clearing DismissedAt would
        // silently resurrect a row the user explicitly X'd out.
        if (prior is not null && prior.StartedAt == startedAt) return;

        _derived.Start(Id, key, startedAt);
        EnsureCatalogReprojected();
        FireReady(key, chestInternalName, durationOverride: duration, atUtc: startedAt + duration);
    }

    /// <summary>
    /// Apply a cooldown-rejection observation: cache the discovered duration
    /// and (optionally) anchor a row from the rejection timestamp.
    ///
    /// <para><b>Default (chest grammar — forward-looking).</b> The
    /// rejection text says <c>"It will refill N hours after you looted
    /// it."</c>, so the cooldown is anchored at the loot time the row's
    /// existing <c>StartedAt</c> already records. Just update the duration;
    /// re-fire <see cref="TimerReady"/> if the new FiringAt has already
    /// elapsed. Today's behaviour preserved.</para>
    ///
    /// <para><b><paramref name="anchorFromRejection"/> = true (cow / other
    /// relative-past grammars).</b> The rejection text says
    /// <c>"in the past hour"</c> — the actual cooldown anchor is unknown
    /// (somewhere in <c>(rejectionTime - duration, rejectionTime]</c>) and
    /// the existing row's <c>StartedAt</c> may not be trustworthy. Three
    /// states (#178):
    /// <list type="bullet">
    ///   <item><b>No prior row</b> — create one anchored at the rejection
    ///   timestamp. Conservative: overstates remaining time by up to
    ///   <paramref name="duration"/> but won't fire <c>Ready</c> early.
    ///   Self-corrects on the next successful loot.</item>
    ///   <item><b>Prior row, still in flight</b> (StartedAt + duration &gt; now)
    ///   — leave anchor alone.</item>
    ///   <item><b>Prior row, already past FiringAt but rejection says
    ///   cooldown is still active</b> (stale anchor) — refresh
    ///   <c>StartedAt</c> to the rejection timestamp. Trust the rejection
    ///   over a recorded loot Mithril may have missed.</item>
    /// </list>
    /// </para>
    /// </summary>
    public void OnChestCooldownObserved(string chestInternalName, TimeSpan duration)
        => OnChestCooldownObserved(chestInternalName, duration, _time.GetUtcNow().UtcDateTime);

    public void OnChestCooldownObserved(
        string chestInternalName,
        TimeSpan duration,
        DateTime rejectionTimestampUtc,
        bool anchorFromRejection = false)
    {
        if (string.IsNullOrEmpty(chestInternalName) || duration <= TimeSpan.Zero) return;

        // 1. Cache the duration so future first-time loots stamp with the
        // verified value.
        var durationChanged = false;
        lock (_catalogLock)
        {
            if (!_cache.ChestDurationByInternalName.TryGetValue(chestInternalName, out var existing)
                || existing != duration)
            {
                _cache.ChestDurationByInternalName[chestInternalName] = duration;
                durationChanged = true;
            }
        }

        var key = ChestKey(chestInternalName);
        var prior = _derived.GetProgress(Id, key);
        var rejectionAt = new DateTimeOffset(rejectionTimestampUtc, TimeSpan.Zero);
        var rowChanged = false;
        var learnedAreaStamped = false;

        // 2. Anchor / refresh the row from the rejection timestamp — only
        // for relative-past grammars where the loot time isn't recoverable
        // from the rejection text. For forward-looking grammars (chest),
        // the existing StartedAt is more accurate so we leave it alone.
        if (anchorFromRejection)
        {
            if (prior is null)
            {
                var currentArea = _areaTracker?.CurrentArea;
                lock (_catalogLock)
                {
                    if (!_cache.LearnedChests.TryGetValue(chestInternalName, out var entry))
                    {
                        _cache.LearnedChests[chestInternalName] = new LearnedChest
                        {
                            FirstObservedAt = rejectionTimestampUtc,
                            LastObservedAt = rejectionTimestampUtc,
                            Area = currentArea,
                        };
                        learnedAreaStamped = true;
                    }
                    else if (entry.Area is null && currentArea is not null)
                    {
                        entry.Area = currentArea;
                        learnedAreaStamped = true;
                    }
                }
                _derived.Start(Id, key, rejectionAt);
                rowChanged = true;
            }
            // State-decision gate: read PlayerWorld's clock (#609) so replay
            // produces identical stale-anchor decisions regardless of attach time.
            else if (prior.DismissedAt is null && prior.StartedAt + duration <= (_worldClock?.Now ?? _time.GetUtcNow()))
            {
                // Stale anchor — recorded loot is older than this rejection
                // implies. Refresh the anchor; the rejection guarantees the
                // cooldown is active right now.
                if (prior.StartedAt != rejectionAt)
                {
                    _derived.Start(Id, key, rejectionAt);
                    rowChanged = true;
                }
            }
            // else: prior row still in flight — leave alone.
        }

        if (durationChanged || learnedAreaStamped || rowChanged)
        {
            try { _cacheStore.Save(_cache); } catch { /* best-effort */ }
            EnsureCatalogReprojected();
        }

        // 3. Re-fire ready for any in-flight row whose new effective
        // FiringAt has already elapsed. Covers both the chest "duration
        // upgrade unmasks a past anchor" case and the cow "newly-anchored
        // row already past" case.
        var current = _derived.GetProgress(Id, key);
        if (current is not null && current.DismissedAt is null)
        {
            FireReady(key, chestInternalName,
                durationOverride: duration,
                atUtc: current.StartedAt + duration);
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
    /// Hard-delete a row from the catalog and progress. Used by the UI to
    /// clean up false-positive entries the bracket tracker accreted before
    /// its discrimination signals were complete (e.g. <c>Chair</c>,
    /// <c>NPC_Otis</c>, <c>AppleTree</c>). Unlike <see cref="DerivedTimerProgressService.Dismiss"/>,
    /// which only stamps <c>DismissedAt</c> and lets the next observation
    /// resurrect the row, <c>Forget</c> drops the catalog cache entry too —
    /// the row will only return if a new observation re-discovers it.
    ///
    /// Intentionally no blocklist: re-discovery is a useful signal that the
    /// parser regressed, and a silent suppression list would mask that.
    /// </summary>
    public void Forget(string key)
    {
        if (string.IsNullOrEmpty(key)) return;

        var changed = false;
        lock (_catalogLock)
        {
            if (key.StartsWith("chest:", StringComparison.Ordinal))
            {
                var internalName = key["chest:".Length..];
                if (_cache.LearnedChests.Remove(internalName)) changed = true;
                if (_cache.ChestDurationByInternalName.Remove(internalName)) changed = true;
            }
            else if (key.StartsWith("defeat:", StringComparison.Ordinal))
            {
                var displayName = key["defeat:".Length..];
                if (_cache.LearnedDefeats.Remove(displayName)) changed = true;
            }
        }

        if (!changed) return;
        try { _cacheStore.Save(_cache); } catch { /* best-effort */ }

        // Order: re-project first so the catalog drops the key and the diff
        // emits a single Removed delta. The subsequent progress Remove fires
        // ProgressChanged but produces no delta because the differ only
        // tracks rows present in the (now-empty) new catalog.
        EnsureCatalogReprojected();
        _derived.Remove(Id, key);
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
        EmitDeltas();
    }

    private (TimeSpan duration, bool verified) ResolveDefeatDuration(string displayName)
    {
        if (_calibrationByDisplayName.TryGetValue(displayName, out var cal))
            return (cal.RewardCooldown, true);
        return (PlaceholderDefeatDuration, false);
    }

    private (TimeSpan duration, bool verified) ResolveChestDuration(string internalName)
    {
        if (_cache.ChestDurationByInternalName.TryGetValue(internalName, out var d))
            return (d, true);
        return (PlaceholderChestDuration, false);
    }

    /// <summary>
    /// Resolve a chest's user-facing display name from <c>strings_all.json</c>
    /// (#178). Lookup order:
    /// <list type="number">
    ///   <item><c>npc_&lt;Area&gt;/&lt;InternalName&gt;_Name</c> — area-scoped
    ///   form. Required for prefabs whose friendly name varies by zone, e.g.
    ///   <c>LootChest1</c> in <c>AreaCave1</c> (<i>"Goblin Storage Chest"</i>)
    ///   vs <c>AreaTomb1</c> (<i>"Old Storage Chest"</i>).</item>
    ///   <item><c>npc_&lt;InternalName&gt;_Name</c> — no-area fallback. Used
    ///   for area-agnostic prefabs (the <c>LootBackpackN</c> family) and for
    ///   chests we haven't yet learned the area for.</item>
    ///   <item>The internal name itself — final fallback so an unknown
    ///   entity still renders a legible string.</item>
    /// </list>
    /// </summary>
    private string ResolveChestDisplayName(string internalName, string? area)
    {
        if (_refData is not null)
        {
            if (!string.IsNullOrEmpty(area)
                && _refData.Strings.TryGetValue($"npc_{area}/{internalName}_Name", out var areaScoped)
                && !string.IsNullOrEmpty(areaScoped))
            {
                return areaScoped;
            }
            if (_refData.Strings.TryGetValue($"npc_{internalName}_Name", out var bare)
                && !string.IsNullOrEmpty(bare))
            {
                return bare;
            }
        }
        return internalName;
    }

    /// <summary>
    /// Map a chest's recorded <see cref="LearnedChest.Area"/> to a UI region
    /// label. Area code resolves to its friendly name via
    /// <see cref="IReferenceDataService.Areas"/>; falls back to the raw area
    /// code and ultimately to <c>"Chests"</c> when no area is known yet.
    /// </summary>
    private string ResolveRegion(string? area)
    {
        if (string.IsNullOrEmpty(area)) return "Chests";
        if (_refData?.Areas.TryGetValue(area, out var entry) == true
            && !string.IsNullOrEmpty(entry.FriendlyName))
        {
            return entry.FriendlyName;
        }
        return area;
    }

    private void OnDerivedProgressChanged(object? sender, EventArgs e) => EmitDeltas();

    /// <summary>
    /// Diff the current <c>(catalog, progress)</c> against the last snapshot
    /// and fire <see cref="RowsChanged"/> if there are deltas. Called from
    /// every mutation path so consumers see per-key changes without
    /// re-snapshotting the whole catalog.
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
        // Union of every chest that's been looted (LearnedChests) and every
        // chest whose duration has been observed via rejection
        // (ChestDurationByInternalName) — covers the rejection-only walk-up
        // case where the player saw the screen text without ever looting.
        var chestNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in _cache.LearnedChests.Keys) chestNames.Add(name);
        foreach (var name in _cache.ChestDurationByInternalName.Keys) chestNames.Add(name);

        var list = new List<TimerCatalogEntry>(chestNames.Count + _cache.LearnedDefeats.Count);

        foreach (var internalName in chestNames)
        {
            var (duration, verified) = ResolveChestDuration(internalName);
            _cache.LearnedChests.TryGetValue(internalName, out var learned);
            var area = learned?.Area;
            var displayName = ResolveChestDisplayName(internalName, area);
            var region = ResolveRegion(area);
            list.Add(new TimerCatalogEntry(
                Key: ChestKey(internalName),
                DisplayName: displayName,
                Region: region,
                Duration: duration,
                SourceMetadata: new LootCatalogPayload(
                    LootKind.Chest, internalName, Region: area, IsDurationVerified: verified)));
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
        lock (_catalogLock) _catalog = BuildCatalog();
        EmitDeltas();
    }

    private void FireReady(string key, string displayName, TimeSpan durationOverride, DateTimeOffset atUtc)
    {
        // State-decision gate: read PlayerWorld's clock (#609) so replay
        // fires the same eager-ready events as a live attach.
        if (atUtc <= (_worldClock?.Now ?? _time.GetUtcNow()))
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
