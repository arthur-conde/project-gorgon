namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// Pure aggregation helper for per-second frame-interval windows. Extracted
/// from <see cref="PerfTracerHostedService"/> so the percentile math has a
/// boundary unit test without needing a live WPF dispatcher.
/// </summary>
public static class FrameStats
{
    public const double DefaultStallThresholdMs = 33.0;

    public readonly record struct Summary(int Count, double MeanMs, double P50Ms, double P95Ms, double MaxMs, int StallCount);

    /// <summary>
    /// Compute summary over <paramref name="intervalsMs"/>. Accepts an unsorted
    /// span — clones internally for percentile sort so the caller's buffer is
    /// untouched. Empty input returns an all-zero summary.
    /// </summary>
    public static Summary Compute(ReadOnlySpan<double> intervalsMs, double stallThresholdMs = DefaultStallThresholdMs)
    {
        if (intervalsMs.Length == 0) return new Summary(0, 0, 0, 0, 0, 0);

        var sorted = intervalsMs.ToArray();
        Array.Sort(sorted);

        double sum = 0;
        var stalls = 0;
        for (var i = 0; i < intervalsMs.Length; i++)
        {
            sum += intervalsMs[i];
            if (intervalsMs[i] > stallThresholdMs) stalls++;
        }

        return new Summary(
            Count: intervalsMs.Length,
            MeanMs: sum / intervalsMs.Length,
            P50Ms: Percentile(sorted, 0.50),
            P95Ms: Percentile(sorted, 0.95),
            MaxMs: sorted[^1],
            StallCount: stalls);
    }

    private static double Percentile(double[] sortedAsc, double q)
    {
        if (sortedAsc.Length == 0) return 0;
        var idx = (int)Math.Clamp(Math.Round(q * (sortedAsc.Length - 1)), 0, sortedAsc.Length - 1);
        return sortedAsc[idx];
    }
}
