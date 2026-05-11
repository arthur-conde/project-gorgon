namespace Arwen.Domain;

/// <summary>
/// Why an individual <see cref="GiftObservation"/> looks suspicious. <see cref="None"/>
/// means it doesn't trigger any of the checks; anything else is a hint for the user
/// that this row may be worth reviewing before it keeps weighing on the per-(NPC,item)
/// rate.
/// </summary>
public enum ObservationFlag
{
    None = 0,

    /// <summary>
    /// <see cref="GiftObservation.DerivedRate"/> is more than the configured σ-threshold
    /// away from the mean of its (NPC, item) group, where the group has at least the
    /// configured minimum number of samples and a non-zero standard deviation.
    /// </summary>
    OutlierInItemGroup = 1,
}

/// <summary>Per-observation outlier verdict plus the stats that produced it, so the UI can build a tooltip.</summary>
public sealed record ObservationFlagInfo(
    ObservationFlag Flag,
    double GroupMean,
    double GroupStdDev,
    int GroupSampleCount,
    double SigmasFromMean);

/// <summary>
/// Statistical outlier detector for <see cref="GiftObservation"/>. Pure function: no
/// state, no side effects, no I/O. The view-model calls <see cref="Detect"/> once per
/// refresh and looks up each row's verdict by <see cref="CalibrationService.ObservationKey"/>.
/// </summary>
public static class ObservationFlagDetector
{
    /// <summary>
    /// Flag every observation whose <see cref="GiftObservation.DerivedRate"/> deviates by
    /// more than <paramref name="stdDevThreshold"/> standard deviations from the mean of
    /// its (NpcKey, ItemInternalName) group. Groups with fewer than
    /// <paramref name="minGroupSize"/> samples — or with a zero standard deviation
    /// (perfect agreement; nothing to flag) — produce no flags.
    /// </summary>
    /// <param name="observations">Source observations. Each one is keyed via <see cref="CalibrationService.ObservationKey"/>.</param>
    /// <param name="stdDevThreshold">σ threshold above/below the group mean.</param>
    /// <param name="minGroupSize">Minimum samples in a group before any flag is emitted.</param>
    /// <returns>Map of observation-key → flag info for every observation that fires a flag. Unflagged observations are absent from the dictionary (caller treats missing as <see cref="ObservationFlag.None"/>).</returns>
    public static IReadOnlyDictionary<string, ObservationFlagInfo> Detect(
        IReadOnlyList<GiftObservation> observations,
        double stdDevThreshold = 3.0,
        int minGroupSize = 3)
    {
        var result = new Dictionary<string, ObservationFlagInfo>(StringComparer.Ordinal);
        if (observations.Count == 0) return result;

        var groups = observations.GroupBy(o => (o.NpcKey, o.ItemInternalName));
        foreach (var group in groups)
        {
            var members = group.ToList();
            if (members.Count < minGroupSize) continue;

            var rates = members.Select(o => o.DerivedRate).ToList();
            var mean = rates.Average();
            var variance = rates.Sum(r => (r - mean) * (r - mean)) / (rates.Count - 1); // sample variance (Bessel's correction)
            var stdDev = Math.Sqrt(variance);
            if (stdDev <= 0) continue;

            foreach (var obs in members)
            {
                var sigmas = Math.Abs(obs.DerivedRate - mean) / stdDev;
                if (sigmas <= stdDevThreshold) continue;
                result[CalibrationService.ObservationKey(obs)] = new ObservationFlagInfo(
                    Flag: ObservationFlag.OutlierInItemGroup,
                    GroupMean: mean,
                    GroupStdDev: stdDev,
                    GroupSampleCount: members.Count,
                    SigmasFromMean: sigmas);
            }
        }

        return result;
    }
}
