using System.Windows.Media.Imaging;

namespace Mithril.Shared.Icons;

public interface IIconCacheService
{
    /// <summary>
    /// Returns a WPF-ready image for the given icon ID. If the icon is already
    /// cached (memory or disk) it is returned immediately. Otherwise a shared
    /// placeholder is returned and an async download is queued; when the real
    /// image arrives, <see cref="IconReady"/> fires on the UI thread.
    /// </summary>
    BitmapImage GetOrLoadIcon(int iconId);

    /// <summary>
    /// Non-blocking variant for live per-row UI binding (the <c>IconImage</c>
    /// control). Returns a cached image only if it is already in memory;
    /// otherwise returns the shared placeholder and resolves the icon — disk
    /// decode <em>or</em> download — entirely off the calling thread, raising
    /// <see cref="IconReady"/> when ready. Unlike <see cref="GetOrLoadIcon"/>
    /// this never performs a synchronous PNG decode on the caller's thread, so a
    /// detail view that realises dozens of chips at once doesn't stall the
    /// dispatcher. Callers that must snapshot the real image immediately (share
    /// cards) keep using <see cref="GetOrLoadIcon"/>.
    ///
    /// The default implementation falls back to <see cref="GetOrLoadIcon"/> so
    /// test doubles need not implement it.
    /// </summary>
    BitmapImage GetOrLoadIconDeferred(int iconId) => GetOrLoadIcon(iconId);

    /// <summary>Fired on the UI thread when an icon finishes downloading.</summary>
    event EventHandler<int>? IconReady;

    /// <summary>Number of icons currently on disk.</summary>
    int CachedCount { get; }

    /// <summary>Total size of the on-disk icon cache in bytes.</summary>
    long CacheSizeBytes { get; }

    /// <summary>Delete all cached icon files from disk and clear in-memory cache.</summary>
    Task ClearCacheAsync();

    /// <summary>
    /// Download all known item icons that are not already cached.
    /// Reports progress as (completed, total).
    /// </summary>
    Task DownloadAllAsync(IProgress<(int completed, int total)> progress, CancellationToken ct = default);

    /// <summary>
    /// Synchronous cache-state check. Returns true if the icon is already in memory
    /// or on disk and can be served immediately without a network round-trip. Never
    /// triggers a fetch and does not load anything into memory.
    /// </summary>
    bool IsCached(int iconId);

    /// <summary>
    /// Filter <paramref name="iconIds"/> to the subset that aren't cached yet.
    /// Useful for preload prompts (e.g. the share dialog's icon-preload banner).
    /// Distinct, preserves first-seen order, drops non-positive ids.
    /// </summary>
    IReadOnlyList<int> GetUncachedIcons(IEnumerable<int> iconIds);

    /// <summary>
    /// Fetch the given subset, skipping any already cached or known-terminally-failed.
    /// Completes when every requested icon is in cache or has failed; reports
    /// <c>(completed, total)</c> progress per resolution. <paramref name="total"/> is
    /// the count of icons that actually needed a download — already-cached ids are
    /// excluded so the bar reflects real work.
    /// </summary>
    Task PreloadAsync(
        IEnumerable<int> iconIds,
        IProgress<(int completed, int total)>? progress = null,
        CancellationToken ct = default);
}
