using System.IO;
using System.Text.Json;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Reference;

namespace Arwen.Domain;

/// <summary>
/// Tracks item instance IDs, detects gift events from the log event sequence,
/// records observations, and computes per-keyword category rates.
///
/// Gift detection sequence:
/// 1. ProcessAddItem(InternalName(instanceId)) → build instanceId → InternalName map
/// 2. ProcessStartInteraction(NPC_Key) → set active NPC context
/// 3. ProcessDeleteItem(instanceId) → item removed while talking to NPC → pending gift
/// 4. ProcessDeltaFavor(NPC_Key, delta) → favor gained → correlate with pending gift
/// </summary>
public sealed class CalibrationService
{
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
        // Cap map size to avoid unbounded growth
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

        // Order B: we already have a pending delta — complete the observation now
        if (_pendingDelta is var (npcKey, delta))
        {
            _pendingDelta = null;
            RecordObservation(npcKey, internalName, delta);
            return;
        }

        // Order A: stash the deleted item and wait for DeltaFavor
        _pendingDeletedItem = (instanceId, internalName);
    }

    public void OnDeltaFavor(string npcKey, double delta)
    {
        if (delta <= 0) return;
        if (_activeNpcKey != npcKey) return;

        // Order A: we already have a pending deleted item — complete the observation now
        if (_pendingDeletedItem is var (_, internalName))
        {
            _pendingDeletedItem = null;
            _pendingDelta = null;
            RecordObservation(npcKey, internalName, delta);
            return;
        }

        // Order B: stash the delta and wait for DeleteItem
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

        var match = _giftIndex.MatchItemToNpc(item.Id, npcKey);
        if (match is null)
        {
            _diag?.Trace("Arwen.Calibration", $"Item '{internalName}' doesn't match any preference for {npcKey}");
            return;
        }

        var rate = delta / (match.Pref * (double)item.Value);

        var observation = new GiftObservation
        {
            NpcKey = npcKey,
            ItemInternalName = internalName,
            MatchedKeyword = match.MatchedKeyword,
            ItemValue = (double)item.Value,
            Pref = match.Pref,
            FavorDelta = delta,
            DerivedRate = rate,
            Timestamp = DateTimeOffset.UtcNow,
        };

        _data.Observations.Add(observation);
        RecomputeRates();
        Save();

        _diag?.Info("Arwen.Calibration",
            $"Gift observed: {internalName} → {npcKey}, +{delta} favor, rate={rate:F4} (keyword={match.MatchedKeyword})");

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Rate computation ────────────────────────────────────────────

    private void RecomputeRates()
    {
        // Global rates: one per keyword category, averaged across all NPCs
        _data.Rates = BuildRateTable(_data.Observations.GroupBy(o => o.MatchedKeyword, StringComparer.Ordinal));

        // Per-NPC rates: keyed by "NpcKey|Keyword"
        _data.NpcRates = BuildRateTable(_data.Observations.GroupBy(o => NpcRateKey(o.NpcKey, o.MatchedKeyword), StringComparer.Ordinal));
    }

    private static Dictionary<string, CategoryRate> BuildRateTable(IEnumerable<IGrouping<string, GiftObservation>> groups)
    {
        var rates = new Dictionary<string, CategoryRate>(StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var derivedRates = g.Select(o => o.DerivedRate).ToList();
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

    private static string NpcRateKey(string npcKey, string keyword) => $"{npcKey}|{keyword}";

    /// <summary>Get the calibrated rate for a keyword, or null if not yet observed.</summary>
    public double? GetRate(string keyword) =>
        _data.Rates.TryGetValue(keyword, out var r) ? r.Rate : null;

    /// <summary>Get the NPC-specific rate, falling back to global rate.</summary>
    public double? GetRate(string npcKey, string keyword) =>
        _data.NpcRates.TryGetValue(NpcRateKey(npcKey, keyword), out var npcRate) ? npcRate.Rate
        : _data.Rates.TryGetValue(keyword, out var globalRate) ? globalRate.Rate
        : null;

    /// <summary>Estimate favor for an item given calibration data. Prefers NPC-specific rate if available.</summary>
    public double? EstimateFavor(GiftMatch match, string? npcKey = null)
    {
        var rate = npcKey is not null ? GetRate(npcKey, match.MatchedKeyword) : GetRate(match.MatchedKeyword);
        if (rate is null) return null;
        return match.Pref * match.ItemValue * rate.Value;
    }

    // ── Persistence ─────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(_dataPath)) return;
        try
        {
            var json = File.ReadAllBytes(_dataPath);
            _data = JsonSerializer.Deserialize(json, CalibrationJsonContext.Default.CalibrationData) ?? new();
            RecomputeRates(); // backfill NpcRates from existing observations
            _diag?.Info("Arwen.Calibration", $"Loaded {_data.Observations.Count} observations, {_data.Rates.Count} global rates, {_data.NpcRates.Count} NPC rates");
        }
        catch (Exception ex)
        {
            _diag?.Warn("Arwen.Calibration", $"Failed to load calibration: {ex.Message}");
            _data = new();
        }
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
            Version = 1,
            ContributorNote = contributorNote,
            ExportedAt = DateTimeOffset.UtcNow,
            Observations = _data.Observations,
            Rates = _data.Rates,
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

        // Deduplicate by (NpcKey, ItemInternalName, FavorDelta, Timestamp)
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
