namespace Gandalf.Domain;

/// <summary>
/// Computes <see cref="TimerRowDelta"/> batches by diffing two snapshots of
/// <c>(catalog, progress)</c>. Sources hold the previous snapshot, take a fresh
/// one whenever they would have raised the legacy
/// <see cref="ITimerSource.CatalogChanged"/> / <see cref="ITimerSource.ProgressChanged"/>
/// events, and emit the diff via <see cref="ITimerSource.RowsChanged"/>.
/// </summary>
internal static class TimerRowDeltaDiffer
{
    public static IReadOnlyList<TimerRowDelta> Diff(
        IReadOnlyDictionary<string, TimerCatalogEntry> oldCatalog,
        IReadOnlyDictionary<string, TimerCatalogEntry> newCatalog,
        IReadOnlyDictionary<string, TimerProgressEntry> oldProgress,
        IReadOnlyDictionary<string, TimerProgressEntry> newProgress)
    {
        var deltas = new List<TimerRowDelta>();

        foreach (var (key, entry) in newCatalog)
        {
            newProgress.TryGetValue(key, out var p);
            if (!oldCatalog.TryGetValue(key, out var oldEntry))
            {
                deltas.Add(new TimerRowDelta(key, TimerRowChangeKind.Added, entry, p));
                continue;
            }

            var catalogDiffers = !oldEntry.Equals(entry);
            oldProgress.TryGetValue(key, out var oldP);
            var progressDiffers = !ProgressEquals(oldP, p);

            // CatalogChanged subsumes ProgressChanged — the delta carries both
            // new catalog and new progress, so receivers see the latest state
            // regardless of which kind they branch on. Avoiding two deltas for
            // one row keeps batches small and ordering deterministic.
            if (catalogDiffers)
                deltas.Add(new TimerRowDelta(key, TimerRowChangeKind.CatalogChanged, entry, p));
            else if (progressDiffers)
                deltas.Add(new TimerRowDelta(key, TimerRowChangeKind.ProgressChanged, entry, p));
        }

        foreach (var key in oldCatalog.Keys)
        {
            if (!newCatalog.ContainsKey(key))
                deltas.Add(new TimerRowDelta(key, TimerRowChangeKind.Removed, null, null));
        }

        return deltas;
    }

    private static bool ProgressEquals(TimerProgressEntry? a, TimerProgressEntry? b) =>
        (a, b) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            _ => a.Equals(b),
        };
}
