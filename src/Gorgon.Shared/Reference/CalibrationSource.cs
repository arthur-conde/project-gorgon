namespace Gorgon.Shared.Reference;

/// <summary>
/// How the merger combines the local player's calibration samples with community-aggregated rates
/// fetched from the gorgon-calibration repo.
/// </summary>
public enum CalibrationSource
{
    /// <summary>Use local if it has at least one sample, else community. Default.</summary>
    PreferLocal,

    /// <summary>Weighted mean by sample count; merge min/max; sum sample counts.</summary>
    Blend,

    /// <summary>Use community if present, else local.</summary>
    PreferCommunity,
}
