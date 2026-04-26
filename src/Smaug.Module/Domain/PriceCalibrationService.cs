using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;

namespace Smaug.Domain;

/// <summary>
/// Result of <see cref="PriceCalibrationService.EstimateSellPrice"/>: expected price,
/// which specificity tier supplied it (Absolute / Ratio), and the sample count.
/// </summary>
public sealed record PriceEstimateResult(double Price, string Tier, int SampleCount, double? Ratio = null);

/// <summary>
/// Records vendor sell observations and aggregates them into two rate dictionaries:
/// absolute prices (fixed-Value items) and Value-ratio (variable-Value items).
/// Blends with community-aggregated rates per <see cref="CalibrationSettings"/>.
/// </summary>
public sealed class PriceCalibrationService
{
    /// <summary>Local schema version: shape of <see cref="PriceObservation"/> records on disk.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Wire schema version stamped into <see cref="VendorRatesPayload.SchemaVersion"/> when
    /// exporting community payloads. Decoupled from <see cref="CurrentSchemaVersion"/>:
    /// only bump when the wire shape (<c>VendorRatesPayload</c>) actually changes, not when
    /// a new field appears on per-observation records (which the wire format never carries).
    /// Equal to <see cref="CurrentSchemaVersion"/> today by coincidence — do not unify.
    /// Validated for strict equality by <see cref="ICommunityCalibrationService"/>.
    /// </summary>
    public const int CommunityWireSchemaVersion = 1;

    private readonly IReferenceDataService _refData;
    private readonly ICommunityCalibrationService? _community;
    private readonly CalibrationSettings? _calibrationSettings;
    private readonly IDiagnosticsSink? _diag;
    private readonly string _dataPath;
    private readonly string _observationsPath;

    private PriceCalibrationData _data = new();

    private IReadOnlyDictionary<string, PriceRate> _resolvedAbsolute = new Dictionary<string, PriceRate>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, RatioRate> _resolvedRatio = new Dictionary<string, RatioRate>(StringComparer.Ordinal);

    public PriceCalibrationData Data => _data;
    public IReadOnlyDictionary<string, PriceRate> EffectiveAbsoluteRates => _resolvedAbsolute;
    public IReadOnlyDictionary<string, RatioRate> EffectiveRatioRates => _resolvedRatio;

    public event EventHandler? DataChanged;

    public PriceCalibrationService(
        IReferenceDataService refData,
        string dataDir,
        ICommunityCalibrationService? community = null,
        CalibrationSettings? calibrationSettings = null,
        IDiagnosticsSink? diag = null)
    {
        _refData = refData;
        _community = community;
        _calibrationSettings = calibrationSettings;
        _diag = diag;
        _dataPath = Path.Combine(dataDir, "calibration.json");
        _observationsPath = Path.Combine(dataDir, "observations.json");
        Load();

        if (_community is not null) _community.FileUpdated += OnCommunityFileUpdated;
        if (_calibrationSettings is not null) _calibrationSettings.PropertyChanged += OnCalibrationSettingsChanged;
    }

    private void OnCommunityFileUpdated(object? sender, string key)
    {
        if (key != "smaug") return;
        RebuildResolvedTables();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCalibrationSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CalibrationSettings.Source)) return;
        RebuildResolvedTables();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Observation recording ───────────────────────────────────────────

    /// <summary>
    /// Record a single sell event. Both rate dictionaries are updated: absolute price
    /// keyed by exact InternalName, Value-ratio keyed by keyword bucket (handles
    /// variable-Value loot/augments).
    /// </summary>
    public void RecordObservation(
        string npcKey,
        string internalName,
        long pricePaid,
        string favorTier,
        int civicPrideLevel,
        DateTimeOffset timestamp)
    {
        if (string.IsNullOrEmpty(npcKey) || string.IsNullOrEmpty(internalName)) return;
        if (pricePaid <= 0) return;

        if (!_refData.ItemsByInternalName.TryGetValue(internalName, out var item))
        {
            _diag?.Trace("Smaug.Calibration", $"Unknown item '{internalName}' — skipping observation");
            return;
        }

        var keywordBucket = KeywordBucketResolver.Resolve(item);
        var observation = new PriceObservation
        {
            NpcKey = npcKey,
            InternalName = internalName,
            ItemKeywords = [.. item.Keywords.Select(k => k.Tag)],
            KeywordBucket = keywordBucket,
            BaseValue = item.Value,
            PricePaid = pricePaid,
            FavorTier = favorTier,
            CivicPrideLevel = civicPrideLevel,
            Timestamp = timestamp,
        };

        _data.Observations.Add(observation);
        RecomputeRates();
        Save();

        _diag?.Info("Smaug.Calibration",
            $"Vendor sell: {internalName} → {npcKey} @ {favorTier} for {pricePaid}c " +
            $"(value={item.Value}, ratio={observation.Ratio:F2}, cp={civicPrideLevel})");

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Rate aggregation ────────────────────────────────────────────────

    private void RecomputeRates()
    {
        _data.AbsoluteRates = BuildAbsoluteRates(_data.Observations);
        _data.RatioRates = BuildRatioRates(_data.Observations);
        RebuildResolvedTables();
    }

    internal static Dictionary<string, PriceRate> BuildAbsoluteRates(IEnumerable<PriceObservation> obs) =>
        obs.Where(o => o.PricePaid > 0)
            .GroupBy(o => AbsoluteKey(o.NpcKey, o.InternalName, o.FavorTier, o.CivicPrideBucketKey), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g =>
            {
                var prices = g.Select(o => o.PricePaid).ToList();
                return new PriceRate
                {
                    Key = g.Key,
                    AvgPrice = prices.Average(p => (double)p),
                    SampleCount = prices.Count,
                    MinPrice = prices.Min(),
                    MaxPrice = prices.Max(),
                };
            }, StringComparer.Ordinal);

    internal static Dictionary<string, RatioRate> BuildRatioRates(IEnumerable<PriceObservation> obs) =>
        obs.Where(o => o.BaseValue > 0 && o.PricePaid > 0)
            .GroupBy(o => RatioKey(o.NpcKey, o.KeywordBucket, o.FavorTier, o.CivicPrideBucketKey), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g =>
            {
                var ratios = g.Select(o => o.Ratio).ToList();
                return new RatioRate
                {
                    Key = g.Key,
                    AvgRatio = ratios.Average(),
                    SampleCount = ratios.Count,
                    MinRatio = ratios.Min(),
                    MaxRatio = ratios.Max(),
                };
            }, StringComparer.Ordinal);

    private void RebuildResolvedTables()
    {
        var mode = _calibrationSettings?.Source ?? CalibrationSource.PreferLocal;
        var community = _community?.SmaugRates;

        _resolvedAbsolute = MergeAbsolute(_data.AbsoluteRates, community?.AbsoluteRates, mode);
        _resolvedRatio = MergeRatio(_data.RatioRates, community?.RatioRates, mode);
    }

    private static Dictionary<string, PriceRate> MergeAbsolute(
        Dictionary<string, PriceRate> local,
        Dictionary<string, AbsolutePriceRatePayload>? community,
        CalibrationSource mode)
    {
        var keys = new HashSet<string>(local.Keys, StringComparer.Ordinal);
        if (community is not null) foreach (var k in community.Keys) keys.Add(k);

        var merged = new Dictionary<string, PriceRate>(StringComparer.Ordinal);
        foreach (var k in keys)
        {
            local.TryGetValue(k, out var localRate);
            AbsolutePriceRatePayload? communityPayload = null;
            community?.TryGetValue(k, out communityPayload);
            var resolved = CommunityRatesMerger.ResolveAbsolute(localRate, communityPayload, k, mode);
            if (resolved is not null)
            {
                resolved.Key = k;
                merged[k] = resolved;
            }
        }
        return merged;
    }

    private static Dictionary<string, RatioRate> MergeRatio(
        Dictionary<string, RatioRate> local,
        Dictionary<string, RatioPriceRatePayload>? community,
        CalibrationSource mode)
    {
        var keys = new HashSet<string>(local.Keys, StringComparer.Ordinal);
        if (community is not null) foreach (var k in community.Keys) keys.Add(k);

        var merged = new Dictionary<string, RatioRate>(StringComparer.Ordinal);
        foreach (var k in keys)
        {
            local.TryGetValue(k, out var localRate);
            RatioPriceRatePayload? communityPayload = null;
            community?.TryGetValue(k, out communityPayload);
            var resolved = CommunityRatesMerger.ResolveRatio(localRate, communityPayload, k, mode);
            if (resolved is not null)
            {
                resolved.Key = k;
                merged[k] = resolved;
            }
        }
        return merged;
    }

    // ── Key format ──────────────────────────────────────────────────────

    public static string AbsoluteKey(string npcKey, string internalName, string favorTier, string civicPrideBucket) =>
        $"{npcKey}|{internalName}|{favorTier}|{civicPrideBucket}";

    public static string RatioKey(string npcKey, string keywordBucket, string favorTier, string civicPrideBucket) =>
        $"{npcKey}|{keywordBucket}|{favorTier}|{civicPrideBucket}";

    // ── Estimation ──────────────────────────────────────────────────────

    /// <summary>
    /// Estimate the vendor sell price for an item. Prefers exact absolute rate; falls back
    /// to Value-ratio × item's current Value. Returns null if no data covers the situation.
    /// </summary>
    public PriceEstimateResult? EstimateSellPrice(string npcKey, string internalName, string favorTier, int civicPrideLevel)
    {
        if (!_refData.ItemsByInternalName.TryGetValue(internalName, out var item)) return null;

        var cpBucket = CivicPrideBucket.FromLevel(civicPrideLevel);

        if (_resolvedAbsolute.TryGetValue(AbsoluteKey(npcKey, internalName, favorTier, cpBucket), out var abs))
            return new PriceEstimateResult(abs.AvgPrice, "Absolute", abs.SampleCount);

        var keywordBucket = KeywordBucketResolver.Resolve(item);
        if (_resolvedRatio.TryGetValue(RatioKey(npcKey, keywordBucket, favorTier, cpBucket), out var ratio))
            return new PriceEstimateResult(ratio.AvgRatio * (double)item.Value, "Ratio", ratio.SampleCount, ratio.AvgRatio);

        return null;
    }

    // ── Persistence ─────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            // Storage layout: observations.json holds the source of truth (SmaugObservationLog),
            // calibration.json holds derived aggregates (SmaugAggregatesData). Older releases
            // wrote both into a single calibration.json (PriceCalibrationData with embedded
            // Observations). We detect that legacy shape on load and split forward.
            //
            // Legacy detection is layout-driven (calibration.json carries an `observations`
            // array), not version-driven — Smaug has never bumped its record schema, but the
            // split still needs to fire on every existing user's first post-upgrade load.
            var legacy = TryLoadLegacy(out var legacyHadObservations);
            var observations = TryLoadObservationLog();

            List<PriceObservation> mergedObservations;
            int loadedVersion;
            if (legacyHadObservations && observations is not null)
            {
                // Downgrade-then-upgrade: post-split build wrote observations.json,
                // user reverted to a pre-split build that re-wrote calibration.json
                // with embedded observations, then upgraded again. Both files now
                // carry observations; merge with dedup (ObservationKey).
                mergedObservations = MergeObservations(observations.Observations, legacy!.Observations);
                loadedVersion = Math.Min(observations.Version, legacy.Version);
                _diag?.Info("Smaug.Calibration",
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

            RecomputeRates();

            if (legacyHadObservations)
            {
                // Layout migration: lift observations out of calibration.json into
                // observations.json. One-shot backup of the pre-split file so a user
                // who notices the layout change can recover their original.
                BackupBeforeSplit();
                Save();
            }

            _diag?.Info("Smaug.Calibration",
                $"Loaded {_data.Observations.Count} observations " +
                $"({_data.AbsoluteRates.Count} absolute, {_data.RatioRates.Count} ratio)");
        }
        catch (Exception ex)
        {
            _diag?.Warn("Smaug.Calibration", $"Failed to load calibration: {ex.Message}");
            _data = new();
        }
    }

    /// <summary>
    /// Read legacy single-file <c>calibration.json</c>. Sets <paramref name="hadObservations"/>
    /// to true iff the file existed AND carried a non-empty <c>Observations</c> array — that
    /// signal drives the split-migration path. Aggregates are intentionally ignored: they're
    /// derived, <see cref="RecomputeRates"/> will rebuild them from observations.
    /// </summary>
    private PriceCalibrationData? TryLoadLegacy(out bool hadObservations)
    {
        hadObservations = false;
        if (!File.Exists(_dataPath)) return null;
        try
        {
            var json = File.ReadAllBytes(_dataPath);
            var loaded = JsonSerializer.Deserialize(json, PriceCalibrationJsonContext.Default.PriceCalibrationData);
            if (loaded is null) return null;
            hadObservations = loaded.Observations.Count > 0;
            return loaded;
        }
        catch (Exception ex)
        {
            _diag?.Warn("Smaug.Calibration", $"Failed to read legacy calibration.json: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Read <c>observations.json</c>. If the file exists but can't be parsed, rename it
    /// to <c>observations.json.corrupt.bak</c> and return null — preserves the user's data
    /// for forensics and prevents the next <see cref="Save"/> from silently overwriting
    /// the unparseable file with empty content.
    /// </summary>
    private SmaugObservationLog? TryLoadObservationLog()
    {
        if (!File.Exists(_observationsPath)) return null;
        try
        {
            var json = File.ReadAllBytes(_observationsPath);
            return JsonSerializer.Deserialize(json, PriceCalibrationJsonContext.Default.SmaugObservationLog);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Smaug.Calibration", $"Failed to read observations.json: {ex.Message}; quarantining as .corrupt.bak");
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
            _diag?.Info("Smaug.Calibration", $"Quarantined unparseable observations.json → {corruptPath}");
        }
        catch (Exception ex)
        {
            _diag?.Warn("Smaug.Calibration", $"Failed to quarantine corrupt observations.json: {ex.Message}");
        }
    }

    private static List<PriceObservation> MergeObservations(List<PriceObservation> primary, List<PriceObservation> secondary)
    {
        var keys = new HashSet<string>(primary.Select(ObservationKey), StringComparer.Ordinal);
        var merged = new List<PriceObservation>(primary);
        foreach (var obs in secondary)
        {
            if (keys.Add(ObservationKey(obs)))
                merged.Add(obs);
        }
        return merged;
    }

    /// <summary>
    /// One-shot snapshot of the legacy single-file <c>calibration.json</c> taken at the
    /// moment we split observations out into <c>observations.json</c>. Layout-migration
    /// backup (orthogonal to any future record-schema-migration backups).
    /// </summary>
    private void BackupBeforeSplit()
    {
        try
        {
            var backupPath = $"{_dataPath}.split.bak";
            if (File.Exists(backupPath)) return;
            File.Copy(_dataPath, backupPath);
            _diag?.Info("Smaug.Calibration", $"Wrote pre-split backup: {backupPath}");
        }
        catch (Exception ex)
        {
            _diag?.Warn("Smaug.Calibration", $"Failed to write pre-split backup: {ex.Message}");
        }
    }

    private void Save()
    {
        // Write order matters for crash safety. observations.json is the source of truth;
        // calibration.json is purely derived (RecomputeRates rebuilds it on every load).
        // If we crash after writing observations.json but before writing calibration.json,
        // the next load re-derives consistent aggregates. Save observations first.
        try
        {
            var observationLog = new SmaugObservationLog
            {
                Version = _data.Version,
                Observations = _data.Observations,
            };
            AtomicJsonWriter.Write(_observationsPath, observationLog,
                PriceCalibrationJsonContext.Default.SmaugObservationLog);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Smaug.Calibration", $"Failed to save observations: {ex.Message}");
            return;
        }

        try
        {
            var aggregates = new SmaugAggregatesData
            {
                Version = _data.Version,
                ExportedAt = DateTimeOffset.UtcNow,
                AbsoluteRates = _data.AbsoluteRates,
                RatioRates = _data.RatioRates,
            };
            AtomicJsonWriter.Write(_dataPath, aggregates,
                PriceCalibrationJsonContext.Default.SmaugAggregatesData);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Smaug.Calibration", $"Failed to save aggregates: {ex.Message}");
        }
    }

    /// <summary>
    /// Stable identifier used to dedup observations when both files carry data
    /// (the downgrade-then-upgrade case). PriceObservation has no Quantity or
    /// sequence id; two genuine consecutive sells of the same item to the same
    /// NPC at the same price within the same OS clock tick (~15.6ms on Windows)
    /// will collide on this key. Acceptable: aggregates barely move from one
    /// dropped duplicate, and the alternative is a schema bump for a sequence id.
    /// </summary>
    private static string ObservationKey(PriceObservation o) =>
        $"{o.NpcKey}|{o.InternalName}|{o.PricePaid}|{o.Timestamp:O}";

    // ── Community export ────────────────────────────────────────────────

    /// <summary>
    /// Sanitized export for community sharing: aggregated rates only, no raw observations.
    /// </summary>
    public string ExportCommunityJson(string? contributorNote = null)
    {
        var payload = new VendorRatesPayload
        {
            SchemaVersion = CommunityWireSchemaVersion,
            Module = "smaug",
            ExportedAt = DateTimeOffset.UtcNow,
            ContributorNote = contributorNote,
            AbsoluteRates = _data.AbsoluteRates.ToDictionary(
                kv => kv.Key,
                kv => new AbsolutePriceRatePayload
                {
                    AvgPrice = kv.Value.AvgPrice,
                    SampleCount = kv.Value.SampleCount,
                    MinPrice = kv.Value.MinPrice,
                    MaxPrice = kv.Value.MaxPrice,
                },
                StringComparer.Ordinal),
            RatioRates = _data.RatioRates.ToDictionary(
                kv => kv.Key,
                kv => new RatioPriceRatePayload
                {
                    AvgRatio = kv.Value.AvgRatio,
                    SampleCount = kv.Value.SampleCount,
                    MinRatio = kv.Value.MinRatio,
                    MaxRatio = kv.Value.MaxRatio,
                },
                StringComparer.Ordinal),
        };
        return JsonSerializer.Serialize(payload, CommunityCalibrationJsonContext.Default.VendorRatesPayload);
    }
}
