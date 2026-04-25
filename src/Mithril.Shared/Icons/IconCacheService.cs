using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Icons;

public sealed class IconCacheService : IIconCacheService
{
    private readonly string _cacheDir;
    private readonly HttpClient _http;
    private readonly IReferenceDataService _refData;
    private readonly IDiagnosticsSink? _diag;
    private readonly IconSettings _settings;

    private readonly ConcurrentDictionary<int, BitmapImage> _memCache = new();
    private readonly ConcurrentDictionary<int, Task> _inflight = new();
    private readonly HashSet<int> _failed = new();
    private readonly SemaphoreSlim _downloadGate = new(8, 8);
    private readonly Dispatcher _dispatcher;

    private BitmapImage? _placeholder;

    public IconCacheService(
        string cacheDir,
        HttpClient http,
        IReferenceDataService refData,
        IDiagnosticsSink? diag,
        IconSettings settings)
    {
        _cacheDir = cacheDir;
        _http = http;
        _refData = refData;
        _diag = diag;
        _settings = settings;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        Directory.CreateDirectory(_cacheDir);
        ScanCache();
    }

    private int _cachedCount;
    private long _cacheSizeBytes;

    public event EventHandler<int>? IconReady;

    public int CachedCount => _cachedCount;
    public long CacheSizeBytes => _cacheSizeBytes;

    public BitmapImage GetOrLoadIcon(int iconId)
    {
        if (iconId <= 0 || !_settings.Enabled)
            return GetPlaceholder();

        if (_memCache.TryGetValue(iconId, out var cached))
            return cached;

        var path = GetDiskPath(iconId);
        if (File.Exists(path))
        {
            var img = LoadFromDisk(path);
            if (img is not null)
            {
                _memCache[iconId] = img;
                return img;
            }
        }

        bool isFailed;
        lock (_failed) { isFailed = _failed.Contains(iconId); }
        if (isFailed)
            return GetPlaceholder();

        // Queue download; return placeholder for now.
        _inflight.GetOrAdd(iconId, id =>
            Task.Run(() => DownloadAsync(id)));

        return GetPlaceholder();
    }

    public async Task ClearCacheAsync()
    {
        _memCache.Clear();
        _failed.Clear();

        await Task.Run(() =>
        {
            if (!Directory.Exists(_cacheDir)) return;
            foreach (var file in Directory.EnumerateFiles(_cacheDir, "icon_*.png"))
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }
        });

        ScanCache();
    }

    public async Task DownloadAllAsync(IProgress<(int completed, int total)> progress, CancellationToken ct = default)
    {
        var allIconIds = _refData.Items.Values
            .Select(i => i.IconId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        // Also include recipe icons
        foreach (var r in _refData.Recipes.Values)
        {
            if (r.IconId > 0) allIconIds.Add(r.IconId);
        }

        var unique = allIconIds.Distinct().ToList();
        var needed = unique.Where(id => !File.Exists(GetDiskPath(id))).ToList();
        var total = needed.Count;
        var completed = 0;

        progress.Report((0, total));

        // Process in batches using the existing semaphore
        var tasks = needed.Select(async iconId =>
        {
            ct.ThrowIfCancellationRequested();
            await DownloadSingleAsync(iconId, ct);
            var c = Interlocked.Increment(ref completed);
            progress.Report((c, total));
        });

        await Task.WhenAll(tasks);
        ScanCache();
    }

    private async Task DownloadSingleAsync(int iconId, CancellationToken ct = default)
    {
        if (File.Exists(GetDiskPath(iconId))) return;

        await _downloadGate.WaitAsync(ct);
        try
        {
            // Re-check after acquiring gate
            if (File.Exists(GetDiskPath(iconId))) return;

            var url = BuildUrl(iconId);
            if (string.IsNullOrEmpty(url)) return;

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var path = GetDiskPath(iconId);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best effort — skip failed icons */ }
        finally
        {
            _downloadGate.Release();
        }
    }

    private async Task DownloadAsync(int iconId)
    {
        await _downloadGate.WaitAsync();
        try
        {
            var url = BuildUrl(iconId);
            if (string.IsNullOrEmpty(url))
            {
                MarkFailed(iconId);
                return;
            }

            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                _diag?.Warn("Icons", $"Download failed for icon {iconId}: HTTP {(int)resp.StatusCode}");
                MarkFailed(iconId);
                return;
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var path = GetDiskPath(iconId);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, path, overwrite: true);

            var img = LoadFromDisk(path);
            if (img is not null)
            {
                _memCache[iconId] = img;
                Interlocked.Add(ref _cacheSizeBytes, bytes.LongLength);
                Interlocked.Increment(ref _cachedCount);
                _ = _dispatcher.BeginInvoke(() => IconReady?.Invoke(this, iconId));
            }
        }
        catch (Exception ex)
        {
            _diag?.Warn("Icons", $"Download failed for icon {iconId}: {ex.Message}");
            MarkFailed(iconId);
        }
        finally
        {
            _inflight.TryRemove(iconId, out _);
            _downloadGate.Release();
        }
    }

    private void MarkFailed(int iconId)
    {
        lock (_failed) { _failed.Add(iconId); }
        _inflight.TryRemove(iconId, out _);
    }

    private string BuildUrl(int iconId)
    {
        var version = _refData.GetSnapshot("items").CdnVersion
                      ?? ReferenceDataService.FallbackCdnVersion;
        return _settings.UrlPattern
            .Replace("{version}", version, StringComparison.OrdinalIgnoreCase)
            .Replace("{iconId}", iconId.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private string GetDiskPath(int iconId) =>
        Path.Combine(_cacheDir, $"icon_{iconId}.png");

    private static BitmapImage? LoadFromDisk(string path)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path, UriKind.Absolute);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    private BitmapImage GetPlaceholder()
    {
        if (_placeholder is not null) return _placeholder;

        var uri = new Uri("pack://application:,,,/Mithril.Shared;component/Icons/placeholder.png");
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = uri;
            img.EndInit();
            img.Freeze();
            _placeholder = img;
        }
        catch
        {
            // If the resource is missing, create a tiny transparent image.
            _placeholder = new BitmapImage();
            _placeholder.Freeze();
        }
        return _placeholder;
    }

    private void ScanCache()
    {
        int count = 0;
        long size = 0;
        if (Directory.Exists(_cacheDir))
        {
            foreach (var file in Directory.EnumerateFiles(_cacheDir, "icon_*.png"))
            {
                try
                {
                    size += new FileInfo(file).Length;
                    count++;
                }
                catch { /* best effort */ }
            }
        }
        _cachedCount = count;
        _cacheSizeBytes = size;
    }
}
