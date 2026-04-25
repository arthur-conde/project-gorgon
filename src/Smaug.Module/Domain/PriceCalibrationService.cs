using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

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
    public const int CurrentSchemaVersion = 1;

    private readonly IReferenceDataService _refData;
    private readonly ICommunityCalibrationService? _community;
    private readonly CalibrationSettings? _calibrationSettings;
    private readonly IDiagnosticsSink? _diag;
    private readonly string _dataPath;

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
        if (!File.Exists(_dataPath)) return;
        try
        {
            var json = File.ReadAllBytes(_dataPath);
            _data = JsonSerializer.Deserialize(json, PriceCalibrationJsonContext.Default.PriceCalibrationData) ?? new();
            RecomputeRates();
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

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
            var tmp = _dataPath + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, _data, PriceCalibrationJsonContext.Default.PriceCalibrationData);
            File.Move(tmp, _dataPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Smaug.Calibration", $"Failed to save calibration: {ex.Message}");
        }
    }

    // ── Community export ────────────────────────────────────────────────

    /// <summary>
    /// Sanitized export for community sharing: aggregated rates only, no raw observations.
    /// </summary>
    public string ExportCommunityJson(string? contributorNote = null)
    {
        var payload = new VendorRatesPayload
        {
            SchemaVersion = CurrentSchemaVersion,
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
