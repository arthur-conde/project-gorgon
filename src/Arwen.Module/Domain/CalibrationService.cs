using System.IO;
using System.Text.Json;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Reference;

namespace Arwen.Domain;

/// <summary>Result of <see cref="CalibrationService.EstimateFavor"/> — value plus which specificity tier supplied the rate.</summary>
public sealed record EstimateResult(double Value, string Tier, int SampleCount);

/// <summary>
/// Tracks item instance IDs, detects gift events from the log event sequence,
/// records observations, and computes per-NPC / per-item / per-signature category rates.
///
/// Gift detection sequence:
/// 1. ProcessAddItem(InternalName(instanceId)) → build instanceId → InternalName map
/// 2. ProcessStartInteraction(NPC_Key) → set active NPC context
/// 3. ProcessDeleteItem(instanceId) → item removed while talking to NPC → pending gift
/// 4. ProcessDeltaFavor(NPC_Key, delta) → favor gained → correlate with pending gift
/// </summary>
public sealed class CalibrationService
{
    public const int CurrentSchemaVersion = 2;

    private readonly IReferenceDataService _refData;
    private readonly GiftIndex _giftIndex;
    private readonly IDiagnosticsSink? _diag;
    private readonly string _dataPath;

    // Transient state for gift detection.
    // The game emits DeleteItem and DeltaFavor in EITHER order:
    //   Order A: DeleteItem → DeltaFavor  (item removed first)
    //   Order B: DeltaFavor → DeleteItem  (favor delta first)
    // We handle both by tracking a pending item OR a pending delta.
    private readonly Dictionary<long, string> _instanceMap = new(); // instanceId → InternalName
    private string? _activeNpcKey;
    private (long InstanceId, string InternalName)? _pendingDeletedItem;
    private (string NpcKey, double Delta)? _pendingDelta;

    private CalibrationData _data = new();

    public CalibrationData Data => _data;

    public event EventHandler? DataChanged;

    public CalibrationService(IReferenceDataService refData, GiftIndex giftIndex, string dataDir, IDiagnosticsSink? diag = null)
    {
        _refData = refData;
        _giftIndex = giftIndex;
        _diag = diag;
        _dataPath = Path.Combine(dataDir, "calibration.json");
        Load();
    }

    // ── Log event handlers (called by FavorIngestionService) ─────────

    public void OnItemAdded(string internalName, long instanceId)
    {
        _instanceMap[instanceId] = internalName;
        if (_instanceMap.Count > 10_000)
        {
            var oldest = _instanceMap.Keys.Take(5000).ToList();
            foreach (var k in oldest) _instanceMap.Remove(k);
        }
    }

    public void OnStartInteraction(string npcKey)
    {
        _activeNpcKey = npcKey;
        _pendingDeletedItem = null;
        _pendingDelta = null;
    }

    public void OnItemDeleted(long instanceId)
    {
        if (_activeNpcKey is null) return;
        if (!_instanceMap.TryGetValue(instanceId, out var internalName)) return;
        _instanceMap.Remove(instanceId);

        if (_pendingDelta is var (npcKey, delta))
        {
            _pendingDelta = null;
            RecordObservation(npcKey, internalName, delta);
            return;
        }

        _pendingDeletedItem = (instanceId, internalName);
    }

    public void OnDeltaFavor(string npcKey, double delta)
    {
        if (delta <= 0) return;
        if (_activeNpcKey != npcKey) return;

        if (_pendingDeletedItem is var (_, internalName))
        {
            _pendingDeletedItem = null;
            _pendingDelta = null;
            RecordObservation(npcKey, internalName, delta);
            return;
        }

        _pendingDelta = (npcKey, delta);
    }

    private void RecordObservation(string npcKey, string internalName, double delta)
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

        var observation = new GiftObservation
        {
            NpcKey = npcKey,
            ItemInternalName = internalName,
            ItemKeywords = [.. item.Keywords.Select(k => k.Tag)],
            MatchedPreferences = [.. matchedPrefs],
            ItemValue = (double)item.Value,
            FavorDelta = delta,
            Timestamp = DateTimeOffset.UtcNow,
        };

        _data.Observations.Add(observation);
        RecomputeRates();
        Save();

        _diag?.Info("Arwen.Calibration",
            $"Gift observed: {internalName} → {npcKey}, +{delta} favor, rate={observation.DerivedRate:F4} (signature={observation.Signature})");

        DataChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>Legacy accessor: global rate for a single keyword. Prefer <see cref="EstimateFavor"/> for hierarchical lookup.</summary>
    public double? GetRate(string keyword) =>
        _data.KeywordRates.TryGetValue(keyword, out var r) ? r.Rate : null;

    /// <summary>
    /// Estimate favor for an item given calibration data. Walks a specificity hierarchy:
    /// per-(NPC,item) → per-(NPC,preference-signature) → per-NPC baseline → global keyword.
    /// Returns null if no tier has data.
    /// </summary>
    public EstimateResult? EstimateFavor(GiftMatch match, string? npcKey = null)
    {
        if (npcKey is not null)
        {
            var allPrefs = _giftIndex.MatchAllPreferencesForItem(match.ItemId, npcKey);
            var effectivePref = allPrefs.Sum(p => p.Pref);

            // Tier 1: per-item rate
            if (_data.ItemRates.TryGetValue(ItemRateKey(npcKey, InternalNameFromMatch(match)), out var itemRate))
                return new EstimateResult(effectivePref * match.ItemValue * itemRate.Rate, "Item", itemRate.SampleCount);

            // Tier 2: signature rate
            if (allPrefs.Count > 0)
            {
                var signature = GiftObservation.BuildSignature(allPrefs);
                if (_data.SignatureRates.TryGetValue(SignatureRateKey(npcKey, signature), out var sigRate))
                    return new EstimateResult(effectivePref * match.ItemValue * sigRate.Rate, "Signature", sigRate.SampleCount);
            }

            // Tier 3: NPC baseline
            if (_data.NpcRates.TryGetValue(npcKey, out var npcRate))
                return new EstimateResult(effectivePref * match.ItemValue * npcRate.Rate, "NPC", npcRate.SampleCount);
        }

        // Tier 4 (global fallback): legacy per-keyword. Uses the best-match keyword and its single pref.
        if (_data.KeywordRates.TryGetValue(match.MatchedKeyword, out var kwRate))
            return new EstimateResult(match.Pref * match.ItemValue * kwRate.Rate, "Global", kwRate.SampleCount);

        return null;
    }

    private string InternalNameFromMatch(GiftMatch match) =>
        _giftIndex.GetItem(match.ItemId)?.InternalName ?? "";

    // ── Persistence ─────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(_dataPath)) return;
        try
        {
            var json = File.ReadAllBytes(_dataPath);
            var loaded = JsonSerializer.Deserialize(json, CalibrationJsonContext.Default.CalibrationData) ?? new();
            _data = loaded;

            if (_data.Version < CurrentSchemaVersion)
            {
                var (kept, dropped) = MigrateObservationsToV2(_data.Observations);
                _diag?.Info("Arwen.Calibration",
                    $"Migrating calibration v{_data.Version} → v{CurrentSchemaVersion}: kept {kept.Count}, dropped {dropped}");
                _data.Observations = kept;
                _data.Version = CurrentSchemaVersion;
                RecomputeRates();
                Save();
            }
            else
            {
                RecomputeRates();
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

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
            var tmp = _dataPath + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, _data, CalibrationJsonContext.Default.CalibrationData);
            File.Move(tmp, _dataPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Arwen.Calibration", $"Failed to save calibration: {ex.Message}");
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

        if (imported.Version < CurrentSchemaVersion)
        {
            var (kept, dropped) = MigrateObservationsToV2(imported.Observations);
            _diag?.Info("Arwen.Calibration",
                $"Importing v{imported.Version} payload: migrated {kept.Count}, dropped {dropped}");
            imported.Observations = kept;
            imported.Version = CurrentSchemaVersion;
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
