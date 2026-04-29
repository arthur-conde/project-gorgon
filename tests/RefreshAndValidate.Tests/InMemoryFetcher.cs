namespace Mithril.Tools.RefreshAndValidate.Tests;

/// <summary>
/// Test fetcher that returns JSON from a fixed dictionary. Throws
/// <see cref="KeyNotFoundException"/> on missing files so a test which forgets
/// to seed a spec sees an obvious failure rather than mysterious empty results.
/// </summary>
internal sealed class InMemoryFetcher : IFetcher
{
    private readonly IReadOnlyDictionary<string, string> _files;

    public InMemoryFetcher(IReadOnlyDictionary<string, string> files) => _files = files;

    public Task<string> FetchAsync(string fileName, CancellationToken ct = default)
    {
        if (!_files.TryGetValue(fileName, out var body))
            throw new KeyNotFoundException($"InMemoryFetcher has no body for '{fileName}'.");
        return Task.FromResult(body);
    }
}
