using Microsoft.Extensions.Logging;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Mithril.Reference.Models.Items;
using Mithril.Shared.Collections;
using Arda.Composition;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;

namespace Arwen.Domain;

/// <summary>Result of <see cref="CalibrationService.EstimateFavor"/> — value plus which specificity tier supplied the rate.</summary>
public sealed record EstimateResult(double Value, string Tier, int SampleCount);

/// <summary>
/// Detects gift events from the log event sequence, records observations,
/// and computes per-NPC / per-item / per-signature category rates.
///
/// Gift detection sequence:
/// 1. <see cref="IInventoryAccumulatorState"/> maintains the canonical instanceId → InternalName map
///    (soft-deletes retained, so post-removal lookups succeed)
/// 2. ProcessStartInteraction(NPC_Key) → set active NPC context
/// 3. ProcessDeleteItem(instanceId) → item removed while talking to NPC → pending gift
/// 4. ProcessDeltaFavor(NPC_Key, delta) → favor gained → correlate with pending gift
/// </summary>
public sealed class CalibrationService
{
    /// <summary>Local schema version: shape of <see cref="GiftObservation"/> records on disk.</summary>
    public const int CurrentSchemaVersion = 4;

    /// <summary>
    /// Wire schema version stamped into <see cref="GiftRatesPayload.SchemaVersion"/> when
    /// exporting community payloads. Decoupled from <see cref="CurrentSchemaVersion"/>:
    /// only bump when the wire shape (<c>GiftRatesPayload</c>) actually changes, not when
    /// a new field appears on per-observation records (which the wire format never carries).
    /// Validated for strict equality by <see cref="ICommunityCalibrationService"/>.
    /// </summary>
    public const int CommunityWireSchemaVersion = 2;

    private readonly IReferenceDataService _refData;
    private readonly GiftIndex _giftIndex;
    private readonly IInventoryAccumulatorState _inventory;
    private readonly ISessionComposer? _session;
    private readonly ICommunityCalibrationService? _community;
    private readonly CalibrationSettings? _calibrationSettings;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly string _dataPath;
    private readonly string _observationsPath;
    private readonly TtlObservableCollection<PendingGiftObservation> _pending;
    // Fast-path dedup set: rebuilt on Load and updated incrementally on every
    // persist / delete. Replay-on-relaunch produces the same key shape, so the
    // short-circuit fires without scanning Observations.
    private readonly HashSet<string> _observationKeys = new(StringComparer.Ordinal);

    // Resolved (local ⊕ community) lookup tables. EstimateFavor reads from these.
    // Rebuilt after RecomputeRates, on community FileUpdated, on settings Source change.
    private IReadOnlyDictionary<string, CategoryRate> _resolvedItemRates = new Dictionary<string, CategoryRate>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, CategoryRate> _resolvedSignatureRates = new Dictionary<string, CategoryRate>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, CategoryRate> _resolvedNpcRates = new Dictionary<string, CategoryRate>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, CategoryRate> _resolvedKeywordRates = new Dictionary<string, CategoryRate>(StringComparer.Ordinal);

    // Transient state for gift detection.
    // The game emits DeleteItem and DeltaFavor in EITHER order:
    //   Order A: DeleteItem → DeltaFavor  (item removed first)
    //   Order B: DeltaFavor → DeleteItem  (favor delta first)
    // We handle both by tracking a pending item OR a pending delta. Each
    // transient also carries the log-line timestamp so the persisted
    // GiftObservation.Timestamp is anchored on log truth, not on
    // DateTimeOffset.UtcNow at record time (which would diverge across
    // replays and defeat the dedup key).
    private string? _activeNpcKey;
    private (long InstanceId, string InternalName, DateTimeOffset Timestamp)? _pendingDeletedItem;
    private (string NpcKey, double Delta, DateTimeOffset Timestamp)? _pendingDelta;

    private CalibrationData _data = new();

    public CalibrationData Data => _data;

    /// <summary>Effective item-tier rates (local ⊕ community). Read-only view onto merged data.</summary>
    public IReadOnlyDictionary<string, CategoryRate> EffectiveItemRates => _resolvedItemRates;
    public IReadOnlyDictionary<string, CategoryRate> EffectiveSignatureRates => _resolvedSignatureRates;
    public IReadOnlyDictionary<string, CategoryRate> EffectiveNpcRates => _resolvedNpcRates;
    public IReadOnlyDictionary<string, CategoryRate> EffectiveKeywordRates => _resolvedKeywordRates;

    /// <summary>
    /// Observations that couldn't be persisted because the gifted item is stackable
    /// and the tracker didn't know the stack size. The user is expected to confirm
    /// the quantity via <see cref="ConfirmPending"/>; otherwise entries age out per
    /// the configured TTL. Pure in-memory — restarts drop the list.
    /// </summary>
    public IReadOnlyList<PendingGiftObservation> PendingObservations => _pending.View;

    public event EventHandler? DataChanged;

    /// <summary>
    /// Raised on enqueue, confirm, discard, and TTL eviction. Carries no payload —
    /// consumers re-read <see cref="PendingObservations"/> on each tick.
    /// </summary>
    public event EventHandler? PendingChanged;

    public CalibrationService(
        IReferenceDataService refData,
        GiftIndex giftIndex,
        IInventoryAccumulatorState inventory,
        string dataDir,
        ICommunityCalibrationService? community = null,
        CalibrationSettings? calibrationSettings = null,
        ILogger? logger = null,
        TimeSpan? pendingTtl = null,
        Action<Action>? dispatch = null,
        TimeProvider? time = null,
        ISessionComposer? session = null)
    {
        _refData = refData;
        _giftIndex = giftIndex;
        _inventory = inventory;
        _session = session;
        _community = community;
        _calibrationSettings = calibrationSettings;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _dataPath = Path.Combine(dataDir, "calibration.json");
        _observationsPath = Path.Combine(dataDir, "observations.json");

        var ttl = pendingTtl ?? TimeSpan.FromHours(24);
        if (ttl <= TimeSpan.Zero) ttl = TimeSpan.FromHours(24);
        _pending = new TtlObservableCollection<PendingGiftObservation>(ttl, dispatch ?? (a => a()), time: time);
        _pending.CollectionChanged += OnPendingCollectionChanged;

        Load();

        if (_community is not null)
            _community.FileUpdated += OnCommunityFileUpdated;
        if (_calibrationSettings is not null)
            _calibrationSettings.PropertyChanged += OnCalibrationSettingsChanged;
    }

    private void OnPendingCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => PendingChanged?.Invoke(this, EventArgs.Empty);

    private void OnCommunityFileUpdated(object? sender, string key)
    {
        if (key != "arwen") return;
        RebuildResolvedTables();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCalibrationSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CalibrationSettings.Source)) return;
        RebuildResolvedTables();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Log event handlers (called by FavorIngestionService) ─────────

    /// <summary>
    /// Test-friendly entry point that stamps observations with the current
    /// wall clock. Production code paths use the timestamp-aware overloads
    /// below; the no-timestamp form preserves the pre-#201 contract for
    /// existing test suites that don't care about replay idempotency.
    /// </summary>
    public void OnStartInteraction(string npcKey)
        => OnStartInteraction(npcKey, _time.GetUtcNow());

    public void OnStartInteraction(string npcKey, DateTimeOffset _)
    {
        // Timestamp on StartInteraction is informational only — the actual
        // observation Timestamp is taken from the DeltaFavor event (the
        // log line that carries the favor change). Accepted here so callers
        // can plumb a single timestamp from raw.Timestamp without branching.
        _activeNpcKey = npcKey;
        _pendingDeletedItem = null;
        _pendingDelta = null;
    }

    public void OnItemDeleted(long instanceId)
        => OnItemDeleted(instanceId, _time.GetUtcNow());

    public void OnItemDeleted(long instanceId, DateTimeOffset timestamp)
    {
        if (_activeNpcKey is null) return;
        if (!_inventory.Items.TryGetValue(instanceId, out var entry))
        {
            _logger?.LogTrace($"Delete id={instanceId} unresolved while talking to {_activeNpcKey}");
            return;
        }
        var internalName = entry.InternalName;

        if (_pendingDelta is var (npcKey, delta, deltaTs))
        {
            _pendingDelta = null;
            // DeltaFavor's timestamp is the canonical stamp for the gift.
            RecordObservation(npcKey, instanceId, internalName, delta, deltaTs);
            return;
        }

        _pendingDeletedItem = (instanceId, internalName, timestamp);
    }

    /// <summary>
    /// Production gift-detection entry point. Consumed by
    /// <see cref="State.FavorIngestionService"/>'s
    /// <see cref="Arda.World.Player.Events.GiftAccepted"/> handler — the Arda Npc
    /// handler correlates the <c>ProcessStartInteraction</c> /
    /// <c>ProcessDeleteItem</c> / <c>ProcessDeltaFavor</c> verb triple at L3
    /// dispatch and emits <see cref="GiftAccepted"/>.
    ///
    /// <para>Resolves <c>ItemInternalName</c> from <see cref="IInventoryAccumulatorState"/>.
    /// The accumulator retains soft-deleted entries, so post-removal lookups succeed
    /// even when the game's delete verb fires before the favor delta.</para>
    ///
    /// <para>The legacy <see cref="OnStartInteraction(string)"/> /
    /// <see cref="OnItemDeleted(long)"/> /
    /// <see cref="OnDeltaFavor(string, double)"/> FSM overloads remain for
    /// existing unit tests.</para>
    /// </summary>
    public void OnGiftAccepted(
        string npcKey,
        long itemInstanceId,
        double deltaFavor,
        DateTimeOffset timestamp)
    {
        if (string.IsNullOrEmpty(npcKey)) return;
        if (deltaFavor <= 0) return;

        if (!_inventory.Items.TryGetValue(itemInstanceId, out var entry))
        {
            _logger?.LogTrace($"GiftAccepted for instance {itemInstanceId} — item not in accumulator.");
            return;
        }

        RecordObservation(npcKey, itemInstanceId, entry.InternalName, deltaFavor, timestamp);
    }

    public void OnDeltaFavor(string npcKey, double delta)
        => OnDeltaFavor(npcKey, delta, _time.GetUtcNow());

    public void OnDeltaFavor(string npcKey, double delta, DateTimeOffset timestamp)
    {
        if (delta <= 0) return;
        if (_activeNpcKey != npcKey) return;

        if (_pendingDeletedItem is var (instanceId, internalName, _))
        {
            _pendingDeletedItem = null;
            _pendingDelta = null;
            RecordObservation(npcKey, instanceId, internalName, delta, timestamp);
            return;
        }

        _pendingDelta = (npcKey, delta, timestamp);
    }

    private void RecordObservation(string npcKey, long instanceId, string internalName, double delta, DateTimeOffset timestamp)
    {
        if (!_refData.ItemsByInternalName.TryGetValue(internalName, out var item))
        {
            _logger?.LogTrace($"Unknown item '{internalName}' — skipping observation");
            return;
        }

        if (item.Value <= 0)
        {
            _logger?.LogTrace($"Item '{internalName}' has value 0 — skipping observation");
            return;
        }

        // Stack-aware quantity. Non-stackable items always gift exactly one unit (PG
        // emits one ProcessDeleteItem per InstanceId, and the InstanceId can't carry
        // more). Stackable items with a known size land directly. The unknown-size
        // case (carryover stack from a prior PG session) goes to the pending bucket
        // for user confirmation rather than silently dropping the favor signal.
        int quantity;
        bool isPending = false;
        if (item.MaxStackSize <= 1)
        {
            quantity = 1;
        }
        else if (_inventory.Items.TryGetValue(instanceId, out var invEntry) && invEntry.StackSize > 0)
        {
            quantity = invEntry.StackSize;
        }
        else
        {
            quantity = 1;
            isPending = true;
        }

        var matchedPrefs = _giftIndex.MatchAllPreferencesForItem(item.Id, npcKey);
        if (matchedPrefs.Count == 0)
        {
            _logger?.LogTrace($"Item '{internalName}' doesn't match any preference for {npcKey}");
            return;
        }

        var effectivePref = matchedPrefs.Sum(p => p.Pref);
        if (effectivePref <= 0)
        {
            _logger?.LogTrace($"Item '{internalName}' nets non-positive pref for {npcKey} — skipping");
            return;
        }

        var sessionId = _session?.Current?.SessionId ?? "";

        if (isPending)
        {
            var pending = new PendingGiftObservation
            {
                Id = Guid.NewGuid(),
                NpcKey = npcKey,
                InstanceId = instanceId,
                InternalName = internalName,
                DisplayName = string.IsNullOrEmpty(item.Name) ? internalName : item.Name!,
                IconId = item.IconId,
                FavorDelta = delta,
                ItemValue = (double)item.Value,
                MaxStackSize = item.MaxStackSize,
                MatchedPreferences = [.. matchedPrefs],
                ItemKeywords = ExtractKeywordTags(item),
                Timestamp = timestamp,
                SessionId = sessionId,
            };
            _pending.Add(pending);
            _logger?.LogInformation($"Pending: '{internalName}' → {npcKey} (+{delta} favor) — quantity unknown, awaiting user confirmation.");
            return;
        }

        PersistObservation(npcKey, instanceId, internalName, item, matchedPrefs, delta, quantity, timestamp, sessionId);
    }

    private void PersistObservation(
        string npcKey,
        long instanceId,
        string internalName,
        Item item,
        IReadOnlyList<MatchedPreference> matchedPrefs,
        double delta,
        int quantity,
        DateTimeOffset timestamp,
        string sessionId)
    {
        var observation = new GiftObservation
        {
            NpcKey = npcKey,
            ItemInternalName = internalName,
            ItemKeywords = ExtractKeywordTags(item),
            MatchedPreferences = [.. matchedPrefs],
            ItemValue = (double)item.Value,
            FavorDelta = delta,
            Quantity = quantity,
            Timestamp = timestamp,
            SessionId = sessionId,
            InstanceId = instanceId,
        };

        var key = ObservationKey(observation);
        if (!_observationKeys.Add(key))
        {
            // Replay-on-relaunch: PG re-emits the same DeleteItem + DeltaFavor
            // pair on every Mithril attach within the same session. The first
            // pass persisted this observation; the second pass produces an
            // identical key (same SessionId + InstanceId + log-line ts). Drop
            // silently so SampleCount stays clean.
            _logger?.LogTrace($"Skipped replay of observation {key}");
            return;
        }

        _data.Observations.Add(observation);
        RecomputeRates();
        Save();

        _logger?.LogInformation($"Gift observed: {internalName} → {npcKey}, +{delta} favor, rate={observation.DerivedRate:F4} (signature={observation.Signature})");

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Promote a pending observation into <see cref="CalibrationData.Observations"/>
    /// using the user-supplied <paramref name="quantity"/>. Returns false if the
    /// id is no longer pending (already confirmed, discarded, or TTL-evicted) or
    /// if <paramref name="quantity"/> is outside <c>[1, MaxStackSize]</c>.
    /// </summary>
    public bool ConfirmPending(Guid id, int quantity)
    {
        var entry = _pending.View.FirstOrDefault(p => p.Id == id);
        if (entry is null) return false;
        if (quantity < 1 || quantity > entry.MaxStackSize) return false;

        if (!_refData.ItemsByInternalName.TryGetValue(entry.InternalName, out var item))
        {
            // Reference data drifted out from under us; refuse rather than persist
            // an observation we can't validate.
            _logger?.LogWarning($"ConfirmPending: '{entry.InternalName}' no longer in reference data — discarding instead.");
            _pending.Remove(p => p.Id == id);
            return false;
        }

        _pending.Remove(p => p.Id == id);
        PersistObservation(entry.NpcKey, entry.InstanceId, entry.InternalName, item, entry.MatchedPreferences,
            entry.FavorDelta, quantity, entry.Timestamp, entry.SessionId);
        return true;
    }

    /// <summary>
    /// Drop a pending observation without persisting it. Returns false if the id
    /// isn't in the pending list.
    /// </summary>
    public bool DiscardPending(Guid id)
    {
        return _pending.Remove(p => p.Id == id) > 0;
    }

    /// <summary>
    /// Rebuild <see cref="_observationKeys"/> from <see cref="_data"/>.
    /// Called on every <see cref="Load"/> and on import/merge paths so the
    /// fast-path dedup set in <see cref="PersistObservation"/> stays in sync
    /// with on-disk state.
    /// </summary>
    private void RebuildObservationKeySet()
    {
        _observationKeys.Clear();
        foreach (var obs in _data.Observations) _observationKeys.Add(ObservationKey(obs));
    }

    // ── Rate computation ────────────────────────────────────────────

    private void RecomputeRates()
    {
        // Per-item: most specific — "NpcKey|ItemInternalName"
        _data.ItemRates = BuildRateTable(
            _data.Observations.GroupBy(o => ItemRateKey(o.NpcKey, o.ItemInternalName), StringComparer.Ordinal));

        // Per-signature: items sharing the same matched-preference set — "NpcKey|signature"
        _data.SignatureRates = BuildRateTable(
            _data.Observations.GroupBy(o => SignatureRateKey(o.NpcKey, o.Signature), StringComparer.Ordinal));

        // Per-NPC baseline — "NpcKey"
        _data.NpcRates = BuildRateTable(
            _data.Observations.GroupBy(o => o.NpcKey, StringComparer.Ordinal));

        // Legacy: global per-keyword, keyed by the first matched preference's first keyword.
        _data.KeywordRates = BuildRateTable(
            _data.Observations
                .Where(o => o.MatchedPreferences.Count > 0 && o.MatchedPreferences[0].Keywords.Count > 0)
                .GroupBy(o => o.MatchedPreferences[0].Keywords[0], StringComparer.Ordinal));

        RebuildResolvedTables();
    }

    /// <summary>
    /// Rebuild the effective (local ⊕ community) lookup tables at all four tiers, per the
    /// configured <see cref="CalibrationSource"/>. <see cref="EstimateFavor"/> reads from these.
    /// </summary>
    private void RebuildResolvedTables()
    {
        var mode = _calibrationSettings?.Source ?? CalibrationSource.PreferLocal;
        var community = _community?.ArwenRates;

        _resolvedItemRates = MergeTier(_data.ItemRates, community?.ItemRates, mode);
        _resolvedSignatureRates = MergeTier(_data.SignatureRates, community?.SignatureRates, mode);
        _resolvedNpcRates = MergeTier(_data.NpcRates, community?.NpcRates, mode);
        _resolvedKeywordRates = MergeTier(_data.KeywordRates, community?.KeywordRates, mode);
    }

    private static Dictionary<string, CategoryRate> MergeTier(
        Dictionary<string, CategoryRate> local,
        Dictionary<string, CategoryRatePayload>? community,
        CalibrationSource mode)
    {
        var keys = new HashSet<string>(local.Keys, StringComparer.Ordinal);
        if (community is not null) foreach (var k in community.Keys) keys.Add(k);

        var merged = new Dictionary<string, CategoryRate>(StringComparer.Ordinal);
        foreach (var k in keys)
        {
            local.TryGetValue(k, out var localRate);
            CategoryRatePayload? communityPayload = null;
            community?.TryGetValue(k, out communityPayload);
            var resolved = CommunityRatesMerger.ResolveRate(localRate, communityPayload, k, mode);
            if (resolved is not null)
            {
                resolved.Keyword = k;
                merged[k] = resolved;
            }
        }
        return merged;
    }

    private static Dictionary<string, CategoryRate> BuildRateTable(IEnumerable<IGrouping<string, GiftObservation>> groups)
    {
        var rates = new Dictionary<string, CategoryRate>(StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var derivedRates = g.Select(o => o.DerivedRate).ToList();
            if (derivedRates.Count == 0) continue;
            rates[g.Key] = new CategoryRate
            {
                Keyword = g.Key,
                Rate = derivedRates.Average(),
                SampleCount = derivedRates.Count,
                MinRate = derivedRates.Min(),
                MaxRate = derivedRates.Max(),
            };
        }
        return rates;
    }

    internal static string ItemRateKey(string npcKey, string internalName) => $"{npcKey}|{internalName}";
    internal static string SignatureRateKey(string npcKey, string signature) => $"{npcKey}|{signature}";

    /// <summary>Legacy accessor: global rate for a single keyword (merged with community). Prefer <see cref="EstimateFavor"/>.</summary>
    public double? GetRate(string keyword) =>
        _resolvedKeywordRates.TryGetValue(keyword, out var r) ? r.Rate : null;

    /// <summary>
    /// Estimate favor for an item given calibration data. Walks a specificity hierarchy:
    /// per-(NPC,item) → per-(NPC,preference-signature) → per-NPC baseline → global keyword.
    /// Reads from resolved (local ⊕ community) tables. Returns null if no tier has data.
    /// </summary>
    public EstimateResult? EstimateFavor(GiftMatch match, string? npcKey = null)
    {
        if (npcKey is not null)
        {
            var allPrefs = _giftIndex.MatchAllPreferencesForItem(match.ItemId, npcKey);
            var effectivePref = allPrefs.Sum(p => p.Pref);

            // Tier 1: per-item rate
            if (_resolvedItemRates.TryGetValue(ItemRateKey(npcKey, InternalNameFromMatch(match)), out var itemRate))
                return new EstimateResult(effectivePref * match.ItemValue * itemRate.Rate, "Item", itemRate.SampleCount);

            // Tier 2: signature rate
            if (allPrefs.Count > 0)
            {
                var signature = GiftObservation.BuildSignature(allPrefs);
                if (_resolvedSignatureRates.TryGetValue(SignatureRateKey(npcKey, signature), out var sigRate))
                    return new EstimateResult(effectivePref * match.ItemValue * sigRate.Rate, "Signature", sigRate.SampleCount);
            }

            // Tier 3: NPC baseline
            if (_resolvedNpcRates.TryGetValue(npcKey, out var npcRate))
                return new EstimateResult(effectivePref * match.ItemValue * npcRate.Rate, "NPC", npcRate.SampleCount);
        }

        // Tier 4 (global fallback): legacy per-keyword. Uses the best-match keyword and its single pref.
        if (_resolvedKeywordRates.TryGetValue(match.MatchedKeyword, out var kwRate))
            return new EstimateResult(match.Pref * match.ItemValue * kwRate.Rate, "Global", kwRate.SampleCount);

        return null;
    }

    private string InternalNameFromMatch(GiftMatch match) =>
        _giftIndex.GetItem(match.ItemId)?.InternalName ?? "";

    // ── Persistence ─────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            // Storage layout: observations.json holds the source of truth (ObservationLog),
            // calibration.json holds derived aggregates (AggregatesData). Older releases
            // wrote both into a single calibration.json (CalibrationData with embedded
            // Observations). We detect that legacy shape on load and split forward.
            //
            // Legacy detection is layout-driven, NOT version-driven: a v3 single-file
            // user (current production reality) needs to split too, even though no
            // record-shape migration is required. Bumping the local Version for "this
            // file got split" would conflate the two axes and burn a version number
            // for the next real observation-shape migration.
            var legacy = TryLoadLegacy(out var legacyHadObservations);
            var observations = TryLoadObservationLog();

            List<GiftObservation> mergedObservations;
            int loadedVersion;
            if (legacyHadObservations && observations is not null)
            {
                // Downgrade-then-upgrade: post-split build wrote observations.json,
                // user reverted to a pre-split build that re-wrote calibration.json
                // with embedded observations, then upgraded again. Both files now
                // carry observations; pick neither, merge with dedup (ObservationKey).
                mergedObservations = MergeObservations(observations.Observations, legacy!.Observations);
                loadedVersion = Math.Min(observations.Version, legacy.Version);
                _logger?.LogInformation($"Both observations.json and legacy calibration.json have observations; merged " +
                    $"{observations.Observations.Count} + {legacy.Observations.Count} → {mergedObservations.Count} (deduped).");
            }
            else if (observations is not null)
            {
                mergedObservations = observations.Observations;
                loadedVersion = observations.Version;
            }
            else if (legacy is not null)
            {
                mergedObservations = legacy.Observations;
                loadedVersion = legacy.Version;
            }
            else
            {
                // Fresh install — no files at all. Leave _data at default; nothing to migrate.
                return;
            }

            _data.Observations = mergedObservations;
            _data.Version = loadedVersion;

            var didMigrate = false;
            if (_data.Version < CurrentSchemaVersion)
            {
                // Snapshot the pre-migration file once before we rewrite it. Recovery path
                // if a future migration drops the wrong observations.
                BackupBeforeMigration(_data.Version);
                didMigrate = true;

                if (_data.Version < 2)
                {
                    var (kept, dropped) = MigrateObservationsToV2(_data.Observations);
                    _logger?.LogInformation($"Migrating calibration v{_data.Version} → v2: kept {kept.Count}, dropped {dropped}");
                    _data.Observations = kept;
                    _data.Version = 2;
                }

                if (_data.Version < 3)
                {
                    var (kept, dropped) = MigrateObservationsToV3(_data.Observations);
                    _logger?.LogInformation($"Migrating calibration v{_data.Version} → v3: kept {kept.Count}, dropped {dropped} (stackable items)");
                    _data.Observations = kept;
                    _data.Version = 3;
                }

                if (_data.Version < 4)
                {
                    // v3 → v4 introduces SessionId + InstanceId on GiftObservation.
                    // Legacy records get SessionId="" and InstanceId=0 — they keep
                    // their (wall-clock-stamped) Timestamp so ObservationKey still
                    // collapses to a stable identity for them. We accept the bloat
                    // pre-fix: existing duplicates stay on disk; future replays
                    // dedup correctly under the new v4 key shape.
                    foreach (var obs in _data.Observations)
                    {
                        obs.SessionId ??= "";
                        // InstanceId defaults to 0 on the property — no-op for clarity.
                    }
                    _logger?.LogInformation($"Migrating calibration v{_data.Version} → v4: {_data.Observations.Count} observations carried forward (legacy session/instance fields default).");
                    _data.Version = 4;
                }
            }

            RebuildObservationKeySet();
            RecomputeRates();

            if (legacyHadObservations)
            {
                // Layout migration: lift observations out of calibration.json into
                // observations.json. One-shot backup of the pre-split file so a user
                // who notices the layout change can recover their original.
                BackupBeforeSplit();
                Save();
            }
            else if (didMigrate)
            {
                Save();
            }

            _logger?.LogInformation($"Loaded {_data.Observations.Count} observations " +
                $"({_data.ItemRates.Count} item rates, {_data.SignatureRates.Count} signature rates, " +
                $"{_data.NpcRates.Count} NPC baselines, {_data.KeywordRates.Count} keyword rates)");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load calibration");
            _data = new();
        }
    }

    /// <summary>
    /// Read legacy single-file <c>calibration.json</c>. Sets <paramref name="hadObservations"/>
    /// to true iff the file existed AND carried a non-empty <c>Observations</c> array — that
    /// signal drives the split-migration path. Aggregates are intentionally ignored: they're
    /// derived, <see cref="RecomputeRates"/> will rebuild them from observations.
    /// </summary>
    private CalibrationData? TryLoadLegacy(out bool hadObservations)
    {
        hadObservations = false;
        if (!File.Exists(_dataPath)) return null;
        try
        {
            var json = File.ReadAllBytes(_dataPath);
            var loaded = JsonSerializer.Deserialize(json, CalibrationJsonContext.Default.CalibrationData);
            if (loaded is null) return null;
            hadObservations = loaded.Observations.Count > 0;
            return loaded;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read legacy calibration.json");
            return null;
        }
    }

    /// <summary>
    /// Read <c>observations.json</c>. If the file exists but can't be parsed, rename it
    /// to <c>observations.json.corrupt.bak</c> and return null — preserves the user's data
    /// for forensics and prevents the next <see cref="Save"/> from silently overwriting
    /// the unparseable file with empty content (the file would otherwise be reconstructed
    /// from whatever observations were salvaged from legacy <c>calibration.json</c>, or
    /// from an empty list on fresh state).
    /// </summary>
    private ObservationLog? TryLoadObservationLog()
    {
        if (!File.Exists(_observationsPath)) return null;
        try
        {
            var json = File.ReadAllBytes(_observationsPath);
            return JsonSerializer.Deserialize(json, CalibrationJsonContext.Default.ObservationLog);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read observations.json: {Message}; quarantining as .corrupt.bak", ex.Message);
            QuarantineCorruptObservations();
            return null;
        }
    }

    private void QuarantineCorruptObservations()
    {
        try
        {
            var corruptPath = _observationsPath + ".corrupt.bak";
            // Don't clobber an existing corrupt backup — if the user already has one,
            // they're investigating; preserve the original instead.
            if (File.Exists(corruptPath)) return;
            File.Move(_observationsPath, corruptPath);
            _logger?.LogInformation($"Quarantined unparseable observations.json → {corruptPath}");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to quarantine corrupt observations.json");
        }
    }

    private static List<GiftObservation> MergeObservations(List<GiftObservation> primary, List<GiftObservation> secondary)
    {
        var keys = new HashSet<string>(primary.Select(ObservationKey), StringComparer.Ordinal);
        var merged = new List<GiftObservation>(primary);
        foreach (var obs in secondary)
        {
            if (keys.Add(ObservationKey(obs)))
                merged.Add(obs);
        }
        return merged;
    }

    /// <summary>
    /// Snapshot the on-disk calibration file before a migration rewrites it. Names the
    /// backup <c>calibration.v{N}.bak</c> where N is the pre-migration version. Skipped
    /// if a backup at that path already exists (don't clobber an older snapshot).
    /// </summary>
    private void BackupBeforeMigration(int preMigrationVersion)
    {
        try
        {
            var backupPath = $"{_dataPath}.v{preMigrationVersion}.bak";
            if (File.Exists(backupPath)) return;
            File.Copy(_dataPath, backupPath);
            _logger?.LogInformation($"Wrote pre-migration backup: {backupPath}");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to write pre-migration backup");
        }
    }

    /// <summary>
    /// One-shot snapshot of the legacy single-file <c>calibration.json</c> taken at the
    /// moment we split observations out into <c>observations.json</c>. Orthogonal to
    /// version-migration backups (which key off observation-record schema): the split
    /// is a layout change, not a record-shape change.
    /// </summary>
    private void BackupBeforeSplit()
    {
        try
        {
            var backupPath = $"{_dataPath}.split.bak";
            if (File.Exists(backupPath)) return;
            File.Copy(_dataPath, backupPath);
            _logger?.LogInformation($"Wrote pre-split backup: {backupPath}");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to write pre-split backup");
        }
    }

    /// <summary>
    /// Re-derive <see cref="GiftObservation.ItemKeywords"/> and <see cref="GiftObservation.MatchedPreferences"/>
    /// for observations that predate schema v2. Observations whose item is no longer in reference data
    /// (or no longer matches any preference for the NPC) are dropped and counted.
    /// </summary>
    private (List<GiftObservation> Kept, int Dropped) MigrateObservationsToV2(List<GiftObservation> legacy)
    {
        var kept = new List<GiftObservation>(legacy.Count);
        var dropped = 0;
        foreach (var obs in legacy)
        {
            // If already populated (e.g. mixed file), keep as-is.
            if (obs.MatchedPreferences.Count > 0 && obs.ItemKeywords.Count > 0)
            {
                kept.Add(obs);
                continue;
            }

            if (!_refData.ItemsByInternalName.TryGetValue(obs.ItemInternalName, out var item))
            {
                _logger?.LogTrace($"Migration: dropping '{obs.ItemInternalName}' (not in reference data)");
                dropped++;
                continue;
            }

            var matchedPrefs = _giftIndex.MatchAllPreferencesForItem(item.Id, obs.NpcKey);
            if (matchedPrefs.Count == 0)
            {
                _logger?.LogTrace($"Migration: dropping '{obs.ItemInternalName}' for {obs.NpcKey} (no matching preferences)");
                dropped++;
                continue;
            }

            obs.ItemKeywords = ExtractKeywordTags(item);
            obs.MatchedPreferences = [.. matchedPrefs];
            // ItemValue / FavorDelta / Timestamp already present from v1.
            kept.Add(obs);
        }
        return (kept, dropped);
    }

    /// <summary>
    /// Drop observations whose item is stackable (<c>MaxStackSize &gt; 1</c>). The pre-v3
    /// schema had no <see cref="GiftObservation.Quantity"/> field and recorded every gift
    /// as quantity 1, but PG emits a single <c>ProcessDeleteItem</c> for an entire gifted
    /// stack — so any stackable-item observation has unknown true quantity and an
    /// over-credited <see cref="GiftObservation.DerivedRate"/>. Surviving observations
    /// (non-stackable items only) get an explicit <c>Quantity = 1</c>.
    /// Items not in reference data are also dropped, mirroring v1→v2 behaviour.
    /// </summary>
    private (List<GiftObservation> Kept, int Dropped) MigrateObservationsToV3(List<GiftObservation> v2)
    {
        var kept = new List<GiftObservation>(v2.Count);
        var dropped = 0;
        foreach (var obs in v2)
        {
            if (!_refData.ItemsByInternalName.TryGetValue(obs.ItemInternalName, out var item))
            {
                _logger?.LogTrace($"v3 migration: dropping '{obs.ItemInternalName}' (not in reference data)");
                dropped++;
                continue;
            }
            if (item.MaxStackSize > 1)
            {
                _logger?.LogTrace($"v3 migration: dropping '{obs.ItemInternalName}' for {obs.NpcKey} (MaxStackSize={item.MaxStackSize}, true gift quantity unrecoverable)");
                dropped++;
                continue;
            }
            obs.Quantity = 1;
            kept.Add(obs);
        }
        return (kept, dropped);
    }

    private void Save()
    {
        // Write order matters for crash safety. observations.json is the source of truth;
        // calibration.json is purely derived (RecomputeRates rebuilds it on every load).
        // If we crash after writing observations.json but before writing calibration.json,
        // the next load re-derives consistent aggregates. The reverse order would leave
        // aggregates referencing observations not yet on disk — a wedge between the two
        // files until the next save. Save observations first.
        try
        {
            var observationLog = new ObservationLog
            {
                Version = _data.Version,
                Observations = _data.Observations,
            };
            AtomicJsonWriter.Write(_observationsPath, observationLog,
                CalibrationJsonContext.Default.ObservationLog);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save observations");
            return;
        }

        try
        {
            var aggregates = new AggregatesData
            {
                Version = _data.Version,
                ExportedAt = DateTimeOffset.UtcNow,
                ItemRates = _data.ItemRates,
                SignatureRates = _data.SignatureRates,
                NpcRates = _data.NpcRates,
                KeywordRates = _data.KeywordRates,
            };
            AtomicJsonWriter.Write(_dataPath, aggregates,
                CalibrationJsonContext.Default.AggregatesData);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save aggregates");
        }
    }

    // ── Export / Import ─────────────────────────────────────────────

    public string ExportJson(string? contributorNote = null)
    {
        var export = new CalibrationData
        {
            Version = CurrentSchemaVersion,
            ContributorNote = contributorNote,
            ExportedAt = DateTimeOffset.UtcNow,
            Observations = _data.Observations,
            ItemRates = _data.ItemRates,
            SignatureRates = _data.SignatureRates,
            NpcRates = _data.NpcRates,
            KeywordRates = _data.KeywordRates,
        };
        return JsonSerializer.Serialize(export, CalibrationJsonContext.Default.CalibrationData);
    }

    /// <summary>
    /// Sanitized export for community sharing: only aggregated rates + sample counts.
    /// No raw observations (which carry item keywords and timestamps), no NPC-specific favor snapshots.
    /// </summary>
    public string ExportCommunityJson(string? contributorNote = null)
    {
        static CategoryRatePayload Project(CategoryRate r) => new()
        {
            Rate = r.Rate,
            SampleCount = r.SampleCount,
            MinRate = r.MinRate,
            MaxRate = r.MaxRate,
        };

        var payload = new GiftRatesPayload
        {
            SchemaVersion = CommunityWireSchemaVersion,
            Module = "arwen",
            ExportedAt = DateTimeOffset.UtcNow,
            ContributorNote = contributorNote,
            ItemRates = _data.ItemRates.ToDictionary(kv => kv.Key, kv => Project(kv.Value), StringComparer.Ordinal),
            SignatureRates = _data.SignatureRates.ToDictionary(kv => kv.Key, kv => Project(kv.Value), StringComparer.Ordinal),
            NpcRates = _data.NpcRates.ToDictionary(kv => kv.Key, kv => Project(kv.Value), StringComparer.Ordinal),
            KeywordRates = _data.KeywordRates.ToDictionary(kv => kv.Key, kv => Project(kv.Value), StringComparer.Ordinal),
        };
        return JsonSerializer.Serialize(payload, CommunityCalibrationJsonContext.Default.GiftRatesPayload);
    }

    public void ExportToFile(string path, string? contributorNote = null)
    {
        var json = ExportJson(contributorNote);
        File.WriteAllText(path, json);
        _logger?.LogInformation($"Exported {_data.Observations.Count} observations to {path}");
    }

    public int ImportJson(string json, bool replaceExisting = false)
    {
        var imported = JsonSerializer.Deserialize(json, CalibrationJsonContext.Default.CalibrationData);
        if (imported is null) return 0;

        if (imported.Version < 2)
        {
            var (kept, dropped) = MigrateObservationsToV2(imported.Observations);
            _logger?.LogInformation($"Importing v{imported.Version} payload → v2: migrated {kept.Count}, dropped {dropped}");
            imported.Observations = kept;
            imported.Version = 2;
        }

        if (imported.Version < 3)
        {
            var (kept, dropped) = MigrateObservationsToV3(imported.Observations);
            _logger?.LogInformation($"Importing v{imported.Version} payload → v3: migrated {kept.Count}, dropped {dropped} (stackable items)");
            imported.Observations = kept;
            imported.Version = 3;
        }

        return MergeData(imported, replaceExisting);
    }

    public int ImportFromFile(string path, bool replaceExisting = false)
    {
        var json = File.ReadAllText(path);
        return ImportJson(json, replaceExisting);
    }

    private int MergeData(CalibrationData incoming, bool replaceExisting)
    {
        if (replaceExisting)
        {
            var count = incoming.Observations.Count;
            _data = incoming;
            RebuildObservationKeySet();
            RecomputeRates();
            Save();
            DataChanged?.Invoke(this, EventArgs.Empty);
            return count;
        }

        var added = 0;
        foreach (var obs in incoming.Observations)
        {
            if (_observationKeys.Add(ObservationKey(obs)))
            {
                _data.Observations.Add(obs);
                added++;
            }
        }

        if (added > 0)
        {
            RecomputeRates();
            Save();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        _logger?.LogInformation($"Imported {added} new observations ({incoming.Observations.Count - added} duplicates skipped)");
        return added;
    }

    /// <summary>
    /// Stable observation identity. Includes <see cref="GiftObservation.SessionId"/>
    /// and <see cref="GiftObservation.InstanceId"/> alongside the legacy
    /// <c>NpcKey|Item|FavorDelta|Timestamp:O</c> shape:
    /// <list type="bullet">
    ///   <item><b>v4 records</b> (<c>SessionId</c> non-empty) dedup on
    ///   session + instance — replay-on-relaunch within the same PG session
    ///   collapses to the original key.</item>
    ///   <item><b>v3 legacy records</b> (<c>SessionId</c> empty,
    ///   <c>InstanceId</c> 0) keep their original key shape — the leading
    ///   <c>|</c>s plus zero are stable, and the trailing
    ///   <c>NpcKey|Item|FavorDelta|Timestamp:O</c> is identical to the
    ///   pre-#201 key, so import-dedup against older exports still works.</item>
    /// </list>
    /// </summary>
    internal static string ObservationKey(GiftObservation o) =>
        $"{o.SessionId}|{o.InstanceId}|{o.NpcKey}|{o.ItemInternalName}|{o.FavorDelta}|{o.Timestamp:O}";

    /// <summary>
    /// Max stack size for the item by <c>InternalName</c>, clamped to a floor of 1 so callers
    /// can use it as the upper bound of a Quantity edit without special-casing non-stackables
    /// (whose reference-data <c>MaxStackSize</c> may be 0 or 1).
    /// Returns 1 if the item is no longer in reference data.
    /// </summary>
    public int GetMaxStackSize(string itemInternalName) =>
        _refData.ItemsByInternalName.TryGetValue(itemInternalName, out var item)
            ? Math.Max(1, item.MaxStackSize)
            : 1;

    /// <summary>
    /// CDN icon id for the item by <c>InternalName</c>. Returns 0 if the item is no longer
    /// in reference data; the icon control treats 0 as "no icon" and renders a placeholder.
    /// </summary>
    public int GetIconId(string itemInternalName) =>
        _refData.ItemsByInternalName.TryGetValue(itemInternalName, out var item)
            ? item.IconId
            : 0;

    /// <summary>
    /// Display name for the item (e.g. "Rubbery Tongue"), falling back to <paramref name="itemInternalName"/>
    /// when the item has dropped out of reference data. Matches the fallback pattern used elsewhere
    /// in <c>CalibrationService</c> (see <see cref="PendingGiftObservation.DisplayName"/> construction).
    /// </summary>
    public string GetItemDisplayName(string itemInternalName) =>
        _refData.ItemsByInternalName.TryGetValue(itemInternalName, out var item) && !string.IsNullOrEmpty(item.Name)
            ? item.Name!
            : itemInternalName;

    /// <summary>
    /// Display name for an NPC. Looks up <see cref="NpcEntry.Name"/> by key; if the NPC is no
    /// longer in reference data, falls back to a key-formatted name (strip <c>NPC_</c> prefix,
    /// underscores → spaces).
    /// </summary>
    public string GetNpcDisplayName(string npcKey)
    {
        if (_refData.Npcs.TryGetValue(npcKey, out var npc) && !string.IsNullOrEmpty(npc.Name))
            return npc.Name;
        var stripped = npcKey.StartsWith("NPC_", StringComparison.Ordinal) ? npcKey[4..] : npcKey;
        return stripped.Replace('_', ' ');
    }

    // ── Edits / Deletes ─────────────────────────────────────────────

    /// <summary>
    /// Delete a single confirmed observation by its <see cref="ObservationKey"/>.
    /// Returns false if the key isn't present. On success, rebuilds aggregates,
    /// persists both files, and fires <see cref="DataChanged"/> — same path
    /// <see cref="PersistObservation"/> takes after recording a new gift.
    /// </summary>
    public bool DeleteObservation(string observationKey)
    {
        var idx = _data.Observations.FindIndex(o => ObservationKey(o) == observationKey);
        if (idx < 0) return false;
        var removed = _data.Observations[idx];
        _data.Observations.RemoveAt(idx);
        _observationKeys.Remove(observationKey);
        RecomputeRates();
        Save();
        _logger?.LogInformation($"Deleted observation: {removed.ItemInternalName} → {removed.NpcKey} (+{removed.FavorDelta} favor, {removed.Timestamp:O})");
        DataChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Update <see cref="GiftObservation.Quantity"/> on a confirmed observation. Returns
    /// false if the key isn't present, the item is no longer in reference data, the
    /// quantity is outside <c>[1, MaxStackSize]</c>, or the value is unchanged. On
    /// success, rebuilds aggregates, persists, and fires <see cref="DataChanged"/>.
    /// </summary>
    public bool UpdateObservationQuantity(string observationKey, int quantity)
    {
        var idx = _data.Observations.FindIndex(o => ObservationKey(o) == observationKey);
        if (idx < 0) return false;
        var obs = _data.Observations[idx];
        if (!_refData.ItemsByInternalName.TryGetValue(obs.ItemInternalName, out var item)) return false;
        var maxStack = Math.Max(1, item.MaxStackSize);
        if (quantity < 1 || quantity > maxStack) return false;
        if (obs.Quantity == quantity) return false;
        var oldQuantity = obs.Quantity;
        obs.Quantity = quantity;
        // Quantity isn't part of ObservationKey, so the key set doesn't change.
        RecomputeRates();
        Save();
        _logger?.LogInformation($"Updated quantity: {obs.ItemInternalName} → {obs.NpcKey} qty {oldQuantity} → {quantity}");
        DataChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Delete every confirmed observation matching <paramref name="predicate"/>. Returns
    /// the count of removed observations. Only triggers recompute/save/DataChanged when
    /// at least one observation was removed.
    /// </summary>
    public int DeleteObservationsByPredicate(Func<GiftObservation, bool> predicate)
    {
        var removed = _data.Observations.RemoveAll(o => predicate(o));
        if (removed == 0) return 0;
        RebuildObservationKeySet();
        RecomputeRates();
        Save();
        _logger?.LogInformation($"Bulk-deleted {removed} observation(s)");
        DataChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>
    /// Flatten an item's raw <see cref="Item.Keywords"/> to a tag-only list for
    /// observation persistence. Tolerates null Keywords (the JSON shape allows
    /// missing-keywords items even though all bundled entries currently have them).
    /// </summary>
    private static List<string> ExtractKeywordTags(Item item)
    {
        if (item.Keywords is null) return [];
        var result = new List<string>(item.Keywords.Count);
        foreach (var kw in item.Keywords) result.Add(kw.Tag);
        return result;
    }
}
