using System.Text.Json.Serialization;

namespace Smaug.Domain;

/// <summary>
/// In-memory union of raw observations and derived aggregates. On disk the two halves
/// live in separate files: <see cref="SmaugObservationLog"/> in <c>observations.json</c>
/// (source of truth), <see cref="SmaugAggregatesData"/> in <c>calibration.json</c>
/// (purely derived — rebuilt from observations on every load via <c>RecomputeRates</c>).
/// <see cref="PriceCalibrationData"/> exists for the runtime VM contract: SellPricesViewModel
/// reads <c>Data.AbsoluteRates</c>/<c>Data.RatioRates</c> and the calibration tab reads
/// <c>Data.Observations</c>.
/// </summary>
public sealed class PriceCalibrationData
{
    public int Version { get; set; } = 1;
    public DateTimeOffset? ExportedAt { get; set; }
    public string? ContributorNote { get; set; }

    public List<PriceObservation> Observations { get; set; } = [];

    /// <summary>Keyed by "NpcKey|InternalName|FavorTier|CivicPrideBucket".</summary>
    public Dictionary<string, PriceRate> AbsoluteRates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keyed by "NpcKey|KeywordBucket|FavorTier|CivicPrideBucket".</summary>
    public Dictionary<string, RatioRate> RatioRates { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// On-disk shape for <c>calibration.json</c>: aggregates only, no observations.
/// Purely derived from <see cref="SmaugObservationLog"/> — rebuilt on every load.
/// Holds LOCAL aggregates only; the merged (local ⊕ community) view lives in
/// <c>EffectiveAbsoluteRates</c>/<c>EffectiveRatioRates</c> and is never persisted.
/// </summary>
public sealed class SmaugAggregatesData
{
    public int Version { get; set; } = 1;
    public DateTimeOffset? ExportedAt { get; set; }
    public Dictionary<string, PriceRate> AbsoluteRates { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, RatioRate> RatioRates { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// On-disk shape for <c>observations.json</c>: the source of truth for vendor pricing.
/// </summary>
public sealed class SmaugObservationLog
{
    public int Version { get; set; } = 1;
    public List<PriceObservation> Observations { get; set; } = [];
}

/// <summary>
/// A single observed sell to a vendor. The price paid is ground truth from
/// <c>ProcessVendorAddItem</c>; Value is cross-referenced against items.json at
/// observation time, so ratio observations are self-contained even if Value
/// changes in a future CDN refresh.
/// </summary>
public sealed class PriceObservation
{
    public string NpcKey { get; set; } = "";
    public string InternalName { get; set; } = "";
    public List<string> ItemKeywords { get; set; } = [];
    public string KeywordBucket { get; set; } = "";
    public decimal BaseValue { get; set; }
    public long PricePaid { get; set; }
    public string FavorTier { get; set; } = "";
    public int CivicPrideLevel { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Ratio of price paid to base Value; undefined when Value is zero.</summary>
    [JsonIgnore]
    public double Ratio => BaseValue > 0 ? (double)PricePaid / (double)BaseValue : 0;

    [JsonIgnore]
    public string CivicPrideBucketKey => Domain.CivicPrideBucket.FromLevel(CivicPrideLevel);
}

public sealed class PriceRate
{
    public double AvgPrice { get; set; }
    public int SampleCount { get; set; }
    public long MinPrice { get; set; }
    public long MaxPrice { get; set; }
    [JsonIgnore] public string Key { get; set; } = "";
}

public sealed class RatioRate
{
    public double AvgRatio { get; set; }
    public int SampleCount { get; set; }
    public double MinRatio { get; set; }
    public double MaxRatio { get; set; }
    [JsonIgnore] public string Key { get; set; } = "";
}
