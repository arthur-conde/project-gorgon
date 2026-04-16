using System.IO;
using System.Net.Http;
using System.Text.Json;
using Gorgon.Shared.Diagnostics;

namespace Gorgon.Shared.Reference;

public sealed class ReferenceDataService : IReferenceDataService
{
    public const string CdnRoot = "https://cdn.projectgorgon.com/";
    public const string FallbackCdnVersion = "v469";

    private readonly string _cacheDir;
    private readonly string _bundledDir;
    private readonly HttpClient _http;
    private readonly IDiagnosticsSink? _diag;

    private IReadOnlyDictionary<long, ItemEntry> _items = new Dictionary<long, ItemEntry>();
    private IReadOnlyDictionary<string, ItemEntry> _itemsByInternalName =
        new Dictionary<string, ItemEntry>(StringComparer.Ordinal);
    private ReferenceFileSnapshot _itemsSnapshot;

    public ReferenceDataService(string cacheDir, HttpClient http, IDiagnosticsSink? diag = null, string? bundledDir = null)
    {
        _cacheDir = cacheDir;
        _http = http;
        _diag = diag;
        _bundledDir = bundledDir ?? Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");

        _itemsSnapshot = new ReferenceFileSnapshot("items", ReferenceFileSource.Bundled, FallbackCdnVersion, null, 0);
        LoadItems();
    }

    public IReadOnlyList<string> Keys { get; } = ["items"];

    public IReadOnlyDictionary<long, ItemEntry> Items => _items;

    public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName => _itemsByInternalName;

    public ReferenceFileSnapshot GetSnapshot(string key) => key switch
    {
        "items" => _itemsSnapshot,
        _ => throw new ArgumentException($"Unknown reference file key: {key}", nameof(key)),
    };

    public event EventHandler<string>? FileUpdated;

    public Task RefreshAsync(string key, CancellationToken ct = default) => key switch
    {
        "items" => RefreshItemsAsync(ct),
        _ => throw new ArgumentException($"Unknown reference file key: {key}", nameof(key)),
    };

    public Task RefreshAllAsync(CancellationToken ct = default) => RefreshItemsAsync(ct);

    public void BeginBackgroundRefresh()
    {
        _ = Task.Run(async () =>
        {
            try { await RefreshAllAsync(CancellationToken.None); }
            catch (Exception ex) { _diag?.Warn("Reference", $"Background refresh failed: {ex.Message}"); }
        });
    }

    private void LoadItems()
    {
        var cachePath = Path.Combine(_cacheDir, "items.json");
        var cacheMetaPath = Path.Combine(_cacheDir, "items.meta.json");

        if (File.Exists(cachePath))
        {
            try
            {
                var meta = TryLoadMetadata(cacheMetaPath, ReferenceFileSource.Cache);
                ParseAndSwap(File.OpenRead(cachePath), meta);
                _diag?.Info("Reference", $"Loaded items from cache ({_items.Count} entries, {meta.CdnVersion}).");
                return;
            }
            catch (Exception ex)
            {
                _diag?.Warn("Reference", $"Cache load failed, falling back to bundled: {ex.Message}");
            }
        }

        var bundledPath = Path.Combine(_bundledDir, "items.json");
        var bundledMetaPath = Path.Combine(_bundledDir, "items.meta.json");
        if (!File.Exists(bundledPath))
        {
            _diag?.Warn("Reference", $"Bundled items.json missing at {bundledPath}.");
            return;
        }
        var bundledMeta = TryLoadMetadata(bundledMetaPath, ReferenceFileSource.Bundled);
        ParseAndSwap(File.OpenRead(bundledPath), bundledMeta);
        _diag?.Info("Reference", $"Loaded items from bundled ({_items.Count} entries, {bundledMeta.CdnVersion}).");
    }

    private ReferenceFileMetadata TryLoadMetadata(string path, ReferenceFileSource defaultSource)
    {
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var meta = JsonSerializer.Deserialize(stream, ReferenceJsonContext.Default.ReferenceFileMetadata);
                if (meta is not null)
                {
                    if (string.IsNullOrEmpty(meta.CdnVersion)) meta.CdnVersion = FallbackCdnVersion;
                    return meta;
                }
            }
            catch { }
        }
        return new ReferenceFileMetadata { CdnVersion = FallbackCdnVersion, Source = defaultSource };
    }

    private void ParseAndSwap(Stream stream, ReferenceFileMetadata meta)
    {
        using var _ = stream;
        var raw = JsonSerializer.Deserialize(stream, ReferenceJsonContext.Default.DictionaryStringRawItem)
                  ?? new Dictionary<string, RawItem>();
        var byId = new Dictionary<long, ItemEntry>(raw.Count);
        var byName = new Dictionary<string, ItemEntry>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, v) in raw)
        {
            // Keys look like "item_10251". Strip the prefix; skip anything we can't parse.
            var underscore = key.IndexOf('_');
            if (underscore < 0) continue;
            if (!long.TryParse(key.AsSpan(underscore + 1), out var id)) continue;
            var entry = new ItemEntry(id, v.Name ?? "", v.InternalName ?? "", v.MaxStackSize ?? 1, v.IconId ?? 0);
            byId[id] = entry;
            if (!string.IsNullOrEmpty(entry.InternalName)) byName[entry.InternalName] = entry;
        }
        _items = byId;
        _itemsByInternalName = byName;
        _itemsSnapshot = new ReferenceFileSnapshot("items", meta.Source, meta.CdnVersion, meta.FetchedAtUtc, byId.Count);
    }

    private async Task RefreshItemsAsync(CancellationToken ct)
    {
        var version = await CdnVersionDetector.TryDetectAsync(_http, CdnRoot, ct)
                      ?? _itemsSnapshot.CdnVersion
                      ?? FallbackCdnVersion;
        var url = $"{CdnRoot}{version}/data/items.json";
        _diag?.Info("Reference", $"Refreshing items from {url}.");

        byte[] body;
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            body = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _diag?.Warn("Reference", $"items.json fetch failed ({ex.Message}); keeping existing data.");
            return;
        }

        var meta = new ReferenceFileMetadata
        {
            CdnVersion = version,
            FetchedAtUtc = DateTimeOffset.UtcNow,
            Source = ReferenceFileSource.Cdn,
        };

        Directory.CreateDirectory(_cacheDir);
        var cachePath = Path.Combine(_cacheDir, "items.json");
        var metaPath = Path.Combine(_cacheDir, "items.meta.json");
        var tmp = cachePath + ".tmp";
        await File.WriteAllBytesAsync(tmp, body, ct);
        File.Move(tmp, cachePath, overwrite: true);

        var metaTmp = metaPath + ".tmp";
        await using (var ms = File.Create(metaTmp))
        {
            await JsonSerializer.SerializeAsync(ms, meta, ReferenceJsonContext.Default.ReferenceFileMetadata, ct);
        }
        File.Move(metaTmp, metaPath, overwrite: true);

        ParseAndSwap(new MemoryStream(body), meta);
        _diag?.Info("Reference", $"items.json refreshed: {_items.Count} entries, version {version}.");
        FileUpdated?.Invoke(this, "items");
    }
}
