namespace Mithril.Tools.RefreshAndValidate;

/// <summary>
/// Production fetcher: pulls each BundledData file from
/// <c>{cdnRoot}{version}/data/{fileName}</c>. A single transient retry is
/// applied per fetch (slice 3 wires up the delay-injectable form for tests).
/// </summary>
public sealed class HttpFetcher : IFetcher
{
    private readonly HttpClient _http;
    private readonly string _cdnRoot;
    private readonly string _version;

    public HttpFetcher(HttpClient http, string cdnRoot, string version)
    {
        _http = http;
        _cdnRoot = cdnRoot.EndsWith('/') ? cdnRoot : cdnRoot + "/";
        _version = version;
    }

    public Task<string> FetchAsync(string fileName, CancellationToken ct = default)
    {
        var url = $"{_cdnRoot}{_version}/data/{fileName}";
        return _http.GetStringAsync(url, ct);
    }
}
