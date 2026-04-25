using System.Text.Json.Serialization;

namespace Smaug.Domain;

/// <summary>
/// On-disk shape persisted to <c>%LocalAppData%/Mithril/Smaug/calibration.json</c>.
/// Contains the raw observation list plus the two aggregated rate dictionaries.
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
