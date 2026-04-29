namespace Mithril.Tools.RefreshAndValidate;

/// <summary>
/// Source of BundledData JSON for the validator. Production wraps an HttpClient;
/// tests use an in-memory map so the suite is hermetic.
/// </summary>
public interface IFetcher
{
    /// <summary>Returns the JSON body for <paramref name="fileName"/> (e.g. "quests.json").</summary>
    Task<string> FetchAsync(string fileName, CancellationToken ct = default);
}
