namespace Mithril.Tools.RefreshAndValidate;

/// <summary>
/// Production fetcher: pulls each BundledData file from
/// <c>{cdnRoot}{version}/data/{fileName}</c>. One transient retry per file;
/// the second failure is propagated to the validator and reported as a fetch
/// failure. The retry delay is constructor-injectable so tests can pass
/// <see cref="TimeSpan.Zero"/> instead of waiting on the wall clock.
/// </summary>
public sealed class HttpFetcher : IFetcher
{
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly string _cdnRoot;
    private readonly string _version;
    private readonly TimeSpan _retryDelay;

    public HttpFetcher(HttpClient http, string cdnRoot, string version)
        : this(http, cdnRoot, version, DefaultRetryDelay) { }

    internal HttpFetcher(HttpClient http, string cdnRoot, string version, TimeSpan retryDelay)
    {
        _http = http;
        _cdnRoot = cdnRoot.EndsWith('/') ? cdnRoot : cdnRoot + "/";
        _version = version;
        _retryDelay = retryDelay;
    }

    public async Task<string> FetchAsync(string fileName, CancellationToken ct = default)
    {
        var url = $"{_cdnRoot}{_version}/data/{fileName}";
        try
        {
            return await _http.GetStringAsync(url, ct);
        }
        catch (Exception first) when (first is HttpRequestException or TaskCanceledException)
        {
            // CDN flake: one second-chance fetch after a short pause. CDN content
            // is small enough that the latency cost is negligible compared to the
            // false-positive cost of failing CI on a single TCP hiccup.
            try { await Task.Delay(_retryDelay, ct); }
            catch (TaskCanceledException) { throw; }

            try
            {
                return await _http.GetStringAsync(url, ct);
            }
            catch (Exception second)
            {
                throw new HttpRequestException(
                    $"Both attempts to fetch {url} failed. First: {first.GetType().Name}: {first.Message}. " +
                    $"Second: {second.GetType().Name}: {second.Message}",
                    second);
            }
        }
    }
}
