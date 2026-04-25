using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Mithril.Shared.Diagnostics;

namespace Mithril.Shared.Reference;

public sealed class CommunityCalibrationService : ICommunityCalibrationService
{
    private const string UrlTemplate =
        "https://raw.githubusercontent.com/arthur-conde/mithril-calibration/main/aggregated/{0}.json";

    // Target schema versions — payloads with mismatches are rejected.
    private const int SamwiseSchemaVersion = 1;
    private const int ArwenSchemaVersion = 2;
    private const int SmaugSchemaVersion = 1;

    private readonly string _cacheDir;
    private readonly HttpClient _http;
    private readonly IDiagnosticsSink? _diag;

    private GrowthRatesPayload? _samwiseRates;
    private GiftRatesPayload? _arwenRates;
    private VendorRatesPayload? _smaugRates;
    private ReferenceFileSnapshot _samwiseSnapshot = new("samwise", ReferenceFileSource.Bundled, "", null, 0);
    private ReferenceFileSnapshot _arwenSnapshot = new("arwen", ReferenceFileSource.Bundled, "", null, 0);
    private ReferenceFileSnapshot _smaugSnapshot = new("smaug", ReferenceFileSource.Bundled, "", null, 0);

    public CommunityCalibrationService(string cacheDir, HttpClient http, IDiagnosticsSink? diag = null)
    {
        _cacheDir = cacheDir;
        _http = http;
        _diag = diag;
        LoadSamwiseFromCache();
        LoadArwenFromCache();
        LoadSmaugFromCache();
    }

    public GrowthRatesPayload? SamwiseRates => _samwiseRates;
    public GiftRatesPayload? ArwenRates => _arwenRates;
    public VendorRatesPayload? SmaugRates => _smaugRates;

    public IReadOnlyList<string> Keys { get; } = ["samwise", "arwen", "smaug"];

    public ReferenceFileSnapshot GetSnapshot(string key) => key switch
    {
        "samwise" => _samwiseSnapshot,
        "arwen" => _arwenSnapshot,
        "smaug" => _smaugSnapshot,
        _ => throw new ArgumentException($"Unknown community-calibration key: {key}", nameof(key)),
    };

    public event EventHandler<string>? FileUpdated;

    public Task RefreshAsync(string key, CancellationToken ct = default) => key switch
    {
        "samwise" => RefreshSamwiseAsync(ct),
        "arwen" => RefreshArwenAsync(ct),
        "smaug" => RefreshSmaugAsync(ct),
        _ => throw new ArgumentException($"Unknown community-calibration key: {key}", nameof(key)),
    };

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await RefreshSamwiseAsync(ct);
        await RefreshArwenAsync(ct);
        await RefreshSmaugAsync(ct);
    }

    public void BeginBackgroundRefresh()
    {
        _ = Task.Run(async () =>
        {
            try { await RefreshAllAsync(CancellationToken.None); }
            catch (Exception ex) { _diag?.Warn("CommunityCalibration", $"Background refresh failed: {ex.Message}"); }
        });
    }

    public void ClearCache()
    {
        TryDeleteCache("samwise");
        TryDeleteCache("arwen");
        TryDeleteCache("smaug");
        _samwiseRates = null;
        _arwenRates = null;
        _smaugRates = null;
        _samwiseSnapshot = new ReferenceFileSnapshot("samwise", ReferenceFileSource.Bundled, "", null, 0);
        _arwenSnapshot = new ReferenceFileSnapshot("arwen", ReferenceFileSource.Bundled, "", null, 0);
        _smaugSnapshot = new ReferenceFileSnapshot("smaug", ReferenceFileSource.Bundled, "", null, 0);
        _diag?.Info("CommunityCalibration", "Cleared cached community calibration.");
        FileUpdated?.Invoke(this, "samwise");
        FileUpdated?.Invoke(this, "arwen");
        FileUpdated?.Invoke(this, "smaug");
    }

    // ── Per-file implementations ────────────────────────────────────────

    private Task RefreshSamwiseAsync(CancellationToken ct) =>
        RefreshFileAsync(
            "samwise",
            CommunityCalibrationJsonContext.Default.GrowthRatesPayload,
            SamwiseSchemaVersion,
            payload =>
            {
                _samwiseRates = payload;
                _samwiseSnapshot = BuildSnapshot("samwise", ReferenceFileSource.Cdn, DateTimeOffset.UtcNow, SamwiseEntryCount(payload));
            },
            ct);

    private Task RefreshArwenAsync(CancellationToken ct) =>
        RefreshFileAsync(
            "arwen",
            CommunityCalibrationJsonContext.Default.GiftRatesPayload,
            ArwenSchemaVersion,
            payload =>
            {
                _arwenRates = payload;
                _arwenSnapshot = BuildSnapshot("arwen", ReferenceFileSource.Cdn, DateTimeOffset.UtcNow, ArwenEntryCount(payload));
            },
            ct);

    private Task RefreshSmaugAsync(CancellationToken ct) =>
        RefreshFileAsync(
            "smaug",
            CommunityCalibrationJsonContext.Default.VendorRatesPayload,
            SmaugSchemaVersion,
            payload =>
            {
                _smaugRates = payload;
                _smaugSnapshot = BuildSnapshot("smaug", ReferenceFileSource.Cdn, DateTimeOffset.UtcNow, SmaugEntryCount(payload));
            },
            ct);

    private void LoadSamwiseFromCache() =>
        LoadFromCache(
            "samwise",
            CommunityCalibrationJsonContext.Default.GrowthRatesPayload,
            SamwiseSchemaVersion,
            (payload, fetchedAt) =>
            {
                _samwiseRates = payload;
                _samwiseSnapshot = BuildSnapshot("samwise", ReferenceFileSource.Cache, fetchedAt, SamwiseEntryCount(payload));
            });

    private void LoadArwenFromCache() =>
        LoadFromCache(
            "arwen",
            CommunityCalibrationJsonContext.Default.GiftRatesPayload,
            ArwenSchemaVersion,
            (payload, fetchedAt) =>
            {
                _arwenRates = payload;
                _arwenSnapshot = BuildSnapshot("arwen", ReferenceFileSource.Cache, fetchedAt, ArwenEntryCount(payload));
            });

    private void LoadSmaugFromCache() =>
        LoadFromCache(
            "smaug",
            CommunityCalibrationJsonContext.Default.VendorRatesPayload,
            SmaugSchemaVersion,
            (payload, fetchedAt) =>
            {
                _smaugRates = payload;
                _smaugSnapshot = BuildSnapshot("smaug", ReferenceFileSource.Cache, fetchedAt, SmaugEntryCount(payload));
            });

    // ── Generic helpers ─────────────────────────────────────────────────

    private async Task RefreshFileAsync<TPayload>(
        string key,
        JsonTypeInfo<TPayload> typeInfo,
        int expectedSchemaVersion,
        Action<TPayload> swap,
        CancellationToken ct)
        where TPayload : class
    {
        var url = string.Format(UrlTemplate, key);
        _diag?.Info("CommunityCalibration", $"Refreshing {key} from {url}.");

        byte[] body;
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            body = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _diag?.Warn("CommunityCalibration", $"{key}.json fetch failed ({ex.Message}); keeping existing data.");
            return;
        }

        TPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize(body, typeInfo)
                   ?? throw new InvalidDataException("Empty payload");
        }
        catch (Exception ex)
        {
            _diag?.Warn("CommunityCalibration", $"{key}.json parse failed ({ex.Message}); keeping existing data.");
            return;
        }

        if (!ValidateSchemaVersion(key, payload, expectedSchemaVersion))
            return;

        Directory.CreateDirectory(_cacheDir);
        var cachePath = Path.Combine(_cacheDir, $"{key}.json");
        var metaPath = Path.Combine(_cacheDir, $"{key}.meta.json");

        await Settings.AtomicFile.WriteAllBytesAtomicAsync(cachePath, body, ct);

        var meta = new ReferenceFileMetadata
        {
            CdnVersion = "",
            FetchedAtUtc = DateTimeOffset.UtcNow,
            Source = ReferenceFileSource.Cdn,
        };
        await Settings.AtomicFile.WriteJsonAtomicAsync(metaPath, meta,
            ReferenceJsonContext.Default.ReferenceFileMetadata, ct);

        swap(payload);
        _diag?.Info("CommunityCalibration", $"{key}.json refreshed.");
        FileUpdated?.Invoke(this, key);
    }

    private void LoadFromCache<TPayload>(
        string key,
        JsonTypeInfo<TPayload> typeInfo,
        int expectedSchemaVersion,
        Action<TPayload, DateTimeOffset?> swap)
        where TPayload : class
    {
        var cachePath = Path.Combine(_cacheDir, $"{key}.json");
        var metaPath = Path.Combine(_cacheDir, $"{key}.meta.json");
        if (!File.Exists(cachePath)) return;

        try
        {
            DateTimeOffset? fetchedAt = null;
            if (File.Exists(metaPath))
            {
                try
                {
                    using var ms = File.OpenRead(metaPath);
                    var meta = JsonSerializer.Deserialize(ms, ReferenceJsonContext.Default.ReferenceFileMetadata);
                    fetchedAt = meta?.FetchedAtUtc;
                }
                catch { /* ignore meta read failures */ }
            }

            using var stream = File.OpenRead(cachePath);
            var payload = JsonSerializer.Deserialize(stream, typeInfo);
            if (payload is null) return;
            if (!ValidateSchemaVersion(key, payload, expectedSchemaVersion)) return;

            swap(payload, fetchedAt);
            _diag?.Info("CommunityCalibration", $"Loaded {key} from cache.");
            FileUpdated?.Invoke(this, key);
        }
        catch (Exception ex)
        {
            _diag?.Warn("CommunityCalibration", $"{key} cache load failed: {ex.Message}");
        }
    }

    private bool ValidateSchemaVersion<TPayload>(string key, TPayload payload, int expected)
        where TPayload : class
    {
        var actual = payload switch
        {
            GrowthRatesPayload g => g.SchemaVersion,
            GiftRatesPayload a => a.SchemaVersion,
            VendorRatesPayload v => v.SchemaVersion,
            _ => -1,
        };
        if (actual == expected) return true;
        _diag?.Warn("CommunityCalibration",
            $"{key}.json schemaVersion {actual} != expected {expected}; ignoring payload.");
        return false;
    }

    private static int SamwiseEntryCount(GrowthRatesPayload p) =>
        p.Rates.Count + p.PhaseRates.Count + p.SlotCapRates.Count;

    private static int ArwenEntryCount(GiftRatesPayload p) =>
        p.ItemRates.Count + p.SignatureRates.Count + p.NpcRates.Count + p.KeywordRates.Count;

    private static int SmaugEntryCount(VendorRatesPayload p) =>
        p.AbsoluteRates.Count + p.RatioRates.Count;

    private static ReferenceFileSnapshot BuildSnapshot(string key, ReferenceFileSource source, DateTimeOffset? fetchedAt, int count) =>
        new(key, source, "", fetchedAt, count);

    private void TryDeleteCache(string key)
    {
        try
        {
            var cachePath = Path.Combine(_cacheDir, $"{key}.json");
            var metaPath = Path.Combine(_cacheDir, $"{key}.meta.json");
            if (File.Exists(cachePath)) File.Delete(cachePath);
            if (File.Exists(metaPath)) File.Delete(metaPath);
        }
        catch (Exception ex)
        {
            _diag?.Warn("CommunityCalibration", $"Failed to delete {key} cache: {ex.Message}");
        }
    }
}
