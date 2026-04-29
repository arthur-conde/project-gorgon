using Mithril.Reference;

namespace Mithril.Tools.RefreshAndValidate;

/// <summary>
/// Runs every <see cref="IParserSpec"/> over JSON pulled from <see cref="IFetcher"/>
/// and prints a per-file summary plus a final pass/fail line. Returns the process
/// exit code: 0 if every file parses, meets its <see cref="IParserSpec.MinimumEntryCount"/>
/// floor, and yields zero <see cref="UnknownReport"/>s; 1 otherwise.
/// </summary>
public static class CdnDriftValidator
{
    public static async Task<int> RunAsync(
        IFetcher fetcher,
        string version,
        IReadOnlyList<IParserSpec> specs,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct = default)
    {
        // Fetch in parallel (small files, but be polite to the CDN — the test harness
        // and live CDN both serve from the same origin).
        var jsonByFile = await FetchAllAsync(fetcher, specs, ct);

        // Validate sequentially so output ordering is stable and matches the
        // alphabetical FileName order ParserRegistry.Discover guarantees.
        var failures = new List<string>();
        foreach (var spec in specs)
        {
            if (!jsonByFile.TryGetValue(spec.FileName, out var fetched))
            {
                failures.Add(spec.FileName);
                stdout.WriteLine($"FAIL {spec.FileName,-40}      fetch failed (no body)");
                continue;
            }

            if (fetched.Error is not null)
            {
                failures.Add(spec.FileName);
                stdout.WriteLine($"FAIL {spec.FileName,-40}      fetch threw: {fetched.Error.GetType().Name}: {fetched.Error.Message}");
                continue;
            }

            try
            {
                var parsed = spec.Parse(fetched.Body!);
                var count = spec.CountEntries(parsed);
                var unknowns = spec.EnumerateUnknowns(parsed).Take(20).ToList();

                var pass = unknowns.Count == 0 && count >= spec.MinimumEntryCount;
                var status = pass ? "OK  " : "FAIL";
                stdout.WriteLine($"{status} {spec.FileName,-40} {count,8} entries  {unknowns.Count} unknowns");

                if (!pass)
                {
                    failures.Add(spec.FileName);
                    if (count < spec.MinimumEntryCount)
                        stdout.WriteLine($"     count {count} < minimum {spec.MinimumEntryCount}");
                    foreach (var u in unknowns)
                        stdout.WriteLine($"     {u}");
                }
            }
            catch (Exception ex)
            {
                failures.Add(spec.FileName);
                stdout.WriteLine($"FAIL {spec.FileName,-40}      parse threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        stdout.WriteLine();
        if (failures.Count == 0)
        {
            stdout.WriteLine($"All {specs.Count} files validated against CDN {version}.");
            return 0;
        }

        stderr.WriteLine($"FAIL: {failures.Count}/{specs.Count} files failed validation against CDN {version}.");
        stderr.WriteLine("Files: " + string.Join(", ", failures));
        return 1;
    }

    private static async Task<Dictionary<string, FetchResult>> FetchAllAsync(
        IFetcher fetcher,
        IReadOnlyList<IParserSpec> specs,
        CancellationToken ct)
    {
        // Cap concurrency at 4 — the CDN is small but there's no reason to hammer it,
        // and a too-wide fanout occasionally trips CDN-side rate-limiting.
        using var gate = new SemaphoreSlim(4);
        var tasks = specs.Select(async spec =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var body = await fetcher.FetchAsync(spec.FileName, ct);
                return (spec.FileName, new FetchResult(body, null));
            }
            catch (Exception ex)
            {
                return (spec.FileName, new FetchResult(null, ex));
            }
            finally
            {
                gate.Release();
            }
        });
        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.FileName, r => r.Item2);
    }

    private readonly record struct FetchResult(string? Body, Exception? Error);
}
