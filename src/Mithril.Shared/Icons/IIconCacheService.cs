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
}
