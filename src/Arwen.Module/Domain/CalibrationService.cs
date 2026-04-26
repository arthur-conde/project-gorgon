using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Mithril.Shared.Collections;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Inventory;
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
/// 1. <see cref="IInventoryService"/> maintains the canonical instanceId → InternalName map
/// 2. ProcessStartInteraction(NPC_Key) → set active NPC context
/// 3. ProcessDeleteItem(instanceId) → item removed while talking to NPC → pending gift
/// 4. ProcessDeltaFavor(NPC_Key, delta) → favor gained → correlate with pending gift
/// </summary>
public sealed class CalibrationService
{
    /// <summary>Local schema version: shape of <see cref="GiftObservation"/> records on disk.</summary>
    public const int CurrentSchemaVersion = 3;

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
    private readonly IInventoryService _inventory;
    private readonly ICommunityCalibrationService? _community;
    private readonly CalibrationSettings? _calibrationSettings;
    private readonly IDiagnosticsSink? _diag;
    private readonly string _dataPath;
    private readonly string _observationsPath;
    private readonly TtlObservableCollection<PendingGiftObservation> _pending;

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
    // We handle both by tracking a pending item OR a pending delta.
    private string? _activeNpcKey;
    private (long InstanceId, string InternalName)? _pendingDeletedItem;
    private (string NpcKey, double Delta)? _pendingDelta;

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
        IInventoryService inventory,
        string dataDir,
        ICommunityCalibrationService? community = null,
        CalibrationSettings? calibrationSettings = null,
        IDiagnosticsSink? diag = null,
        TimeSpan? pendingTtl = null,
        Action<Action>? dispatch = null,
        TimeProvider? time = null)
    {
        _refData = refData;
        _giftIndex = giftIndex;
        _inventory = inventory;
        _community = community;
        _calibrationSettings = calibrationSettings;
        _diag = diag;
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

    public void OnStartInteraction(string npcKey)
    {
        _activeNpcKey = npcKey;
        _pendingDeletedItem = null;
        _pendingDelta = null;
    }

    public void OnItemDeleted(long instanceId)
    {
        if (_activeNpcKey is null) return;
        if (!_inventory.TryResolve(instanceId, out var internalName))
        {
            _diag?.Trace("Arwen.Calibration", $"Delete id={instanceId} unresolved while talking to {_activeNpcKey}");
            return;
        }

        if (_pendingDelta is var (npcKey, delta))
        {
            _pendingDelta = null;
            RecordObservation(npcKey, instanceId, internalName, delta);
            return;
        }

        _pendingDeletedItem = (instanceId, internalName);
    }

    public void OnDeltaFavor(string npcKey, double delta)
    {
        if (delta <= 0) return;
        if (_activeNpcKey != npcKey) return;

        if (_pendingDeletedItem is var (instanceId, internalName))
        {
            _pendingDeletedItem = null;
            _pendingDelta = null;
            RecordObservation(npcKey, instanceId, internalName, delta);
            return;
        }

        _pendingDelta = (npcKey, delta);
    }

    private void RecordObservation(string npcKey, long instanceId, string internalName, double delta)
    {
        if (!_refData.ItemsByInternalName.TryGetValue(internalName, out var item))
        {
            _diag?.Trace("Arwen.Calibration", $"Unknown item '{internalName}' — skipping observation");
            return;
        }

        if (item.Value <= 0)
        {
            _diag?.Trace("Arwen.Calibration", $"Item '{internalName}' has value 0 — skipping observation");
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
        else if (_inventory.TryGetStackSize(instanceId, out var trackedSize) && trackedSize > 0)
        {
            quantity = trackedSize;
        }
        else
        {
            quantity = 1;
            isPending = true;
        }

        var matchedPrefs = _giftIndex.MatchAllPreferencesForItem(item.Id, npcKey);
        if (matchedPrefs.Count == 0)
        {
            _diag?.Trace("Arwen.Calibration", $"Item '{internalName}' doesn't match any preference for {npcKey}");
            return;
        }

        var effectivePref = matchedPrefs.Sum(p => p.Pref);
        if (effectivePref <= 0)
        {
            _diag?.Trace("Arwen.Calibration", $"Item '{internalName}' nets non-positive pref for {npcKey} — skipping");
            return;
        }

        if (isPending)
        {
            var pending = new PendingGiftObservation
            {
                Id = Guid.NewGuid(),
                NpcKey = npcKey,
                InstanceId = instanceId,
                InternalName = internalName,
                DisplayName = string.IsNullOrEmpty(item.Name) ? internalName : item.Name,
                IconId = item.IconId,
                FavorDelta = delta,
                ItemValue = (double)item.Value,
                MaxStackSize = item.MaxStackSize,
                MatchedPreferences = [.. matchedPrefs],
                ItemKeywords = [.. item.Keywords.Select(k => k.Tag)],
                Timestamp = DateTimeOffset.UtcNow,
            };
            _pending.Add(pending);
            _diag?.Info("Arwen.Calibration",
                $"Pending: '{internalName}' → {npcKey} (+{delta} favor) — quantity unknown, awaiting user confirmation.");
            return;
        }

        PersistObservation(npcKey, internalName, item, matchedPrefs, delta, quantity);
    }

    private void PersistObservation(
        string npcKey,
        string internalName,
        ItemEntry item,
        IReadOnlyList<MatchedPreference> matchedPrefs,
        double delta,
        int quantity)
    {
        var observation = new GiftObservation
        {
            NpcKey = npcKey,
            ItemInternalName = internalName,
            ItemKeywords = [.. item.Keywords.Select(k => k.Tag)],
            MatchedPreferences = [.. matchedPrefs],
            ItemValue = (double)item.Value,
            FavorDelta = delta,
            Quantity = quantity,
            Timestamp = DateTimeOffset.UtcNow,
        };

        _data.Observations.Add(observation);
        RecomputeRates();
        Save();

        _diag?.Info("Arwen.Calibration",
            $"Gift observed: {internalName} → {npcKey}, +{delta} favor, rate={observation.DerivedRate:F4} (signature={observation.Signature})");

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
            _diag?.Warn("Arwen.Calibration",
                $"ConfirmPending: '{entry.InternalName}' no longer in reference data — discarding instead.");
            _pending.Remove(p => p.Id == id);
            return false;
        }

        _pending.Remove(p => p.Id == id);
        PersistObservation(entry.NpcKey, entry.InternalName, item, entry.MatchedPreferences, entry.FavorDelta, quantity);
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
                _diag?.Info("Arwen.Calibration",
                    $"Both observations.json and legacy calibration.json have observations; merged " +
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
                    _diag?.Info("Arwen.Calibration",
                        $"Migrating calibration v{_data.Version} → v2: kept {kept.Count}, dropped {dropped}");
                    _data.Observations = kept;
                    _data.Version = 2;
                }

                if (_data.Version < 3)
                {
                    var (kept, dropped) = MigrateObservationsToV3(_data.Observations);
                    _diag?.Info("Arwen.Calibration",
                        $"Migrating calibration v{_data.Version} → v3: kept {kept.Count}, dropped {dropped} (stackable items)");
                    _data.Observations = kept;
                    _data.Version = 3;
                }
            }

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

            _diag?.Info("Arwen.Calibration",
                $"Loaded {_data.Observations.Count} observations " +
                $"({_data.ItemRates.Count} item rates, {_data.SignatureRates.Count} signature rates, " +
                $"{_data.NpcRates.Count} NPC baselines, {_data.KeywordRates.Count} keyword rates)");
        }
        catch (Exception ex)
        {
            _diag?.Warn("Arwen.Calibration", $"Failed to load calibration: {ex.Message}");
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
            _diag?.Warn("Arwen.Calibration", $"Failed to read legacy calibration.json: {ex.Message}");
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
            _diag?.Warn("Arwen.Calibration", $"Failed to read observations.json: {ex.Message}; quarantining as .corrupt.bak");
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
            _diag?.Info("Arwen.Calibration", $"Quarantined unparseable observations.json → {corruptPath}");
        }
        catch (Exception ex)
        {
            _diag?.Warn("Arwen.Calibration", $"Failed to quarantine corrupt observations.json: {ex.Message}");
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
            _diag?.Info("Arwen.Calibration", $"Wrote pre-migration backup: {backupPath}");
        }
        catch (Exception ex)
        {
            _diag?.Warn("Arwen.Calibration", $"Failed to write pre-migration backup: {ex.Message}");
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
            _diag?.Info("Arwen.Calibration", $"Wrote pre-split backup: {backupPath}");
        }
        catch (Exception ex)
        {
            _diag?.Warn("Arwen.Calibration", $"Failed to write pre-split backup: {ex.Message}");
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
                _diag?.Trace("Arwen.Calibration", $"Migration: dropping '{obs.ItemInternalName}' (not in reference data)");
                dropped++;
                continue;
            }

            var matchedPrefs = _giftIndex.MatchAllPreferencesForItem(item.Id, obs.NpcKey);
            if (matchedPrefs.Count == 0)
            {
                _diag?.Trace("Arwen.Calibration", $"Migration: dropping '{obs.ItemInternalName}' for {obs.NpcKey} (no matching preferences)");
                dropped++;
                continue;
            }

            obs.ItemKeywords = [.. item.Keywords.Select(k => k.Tag)];
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
                _diag?.Trace("Arwen.Calibration", $"v3 migration: dropping '{obs.ItemInternalName}' (not in reference data)");
                dropped++;
                continue;
            }
            if (item.MaxStackSize > 1)
            {
                _diag?.Trace("Arwen.Calibration",
                    $"v3 migration: dropping '{obs.ItemInternalName}' for {obs.NpcKey} (MaxStackSize={item.MaxStackSize}, true gift quantity unrecoverable)");
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
            _diag?.Warn("Arwen.Calibration", $"Failed to save observations: {ex.Message}");
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
            _diag?.Warn("Arwen.Calibration", $"Failed to save aggregates: {ex.Message}");
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
        _diag?.Info("Arwen.Calibration", $"Exported {_data.Observations.Count} observations to {path}");
    }

    public int ImportJson(string json, bool replaceExisting = false)
    {
        var imported = JsonSerializer.Deserialize(json, CalibrationJsonContext.Default.CalibrationData);
        if (imported is null) return 0;

        if (imported.Version < 2)
        {
            var (kept, dropped) = MigrateObservationsToV2(imported.Observations);
            _diag?.Info("Arwen.Calibration",
                $"Importing v{imported.Version} payload → v2: migrated {kept.Count}, dropped {dropped}");
            imported.Observations = kept;
            imported.Version = 2;
        }

        if (imported.Version < 3)
        {
            var (kept, dropped) = MigrateObservationsToV3(imported.Observations);
            _diag?.Info("Arwen.Calibration",
                $"Importing v{imported.Version} payload → v3: migrated {kept.Count}, dropped {dropped} (stackable items)");
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
            RecomputeRates();
            Save();
            DataChanged?.Invoke(this, EventArgs.Empty);
            return count;
        }

        var existingKeys = new HashSet<string>(
            _data.Observations.Select(ObservationKey));
        var added = 0;
        foreach (var obs in incoming.Observations)
        {
            if (existingKeys.Add(ObservationKey(obs)))
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

        _diag?.Info("Arwen.Calibration", $"Imported {added} new observations ({incoming.Observations.Count - added} duplicates skipped)");
        return added;
    }

    private static string ObservationKey(GiftObservation o) =>
        $"{o.NpcKey}|{o.ItemInternalName}|{o.FavorDelta}|{o.Timestamp:O}";
}
