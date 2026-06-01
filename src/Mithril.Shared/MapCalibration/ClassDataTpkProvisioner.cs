using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Mithril.Shared.Settings;

namespace Mithril.Shared.MapCalibration;

/// <summary>
/// BCL-only (HttpClient + SHA256 + File IO) provisioner for the UABEA
/// <c>classdata.tpk</c> the asset-extractor sidecar needs (#960). No decoder /
/// AssetsTools.NET dependency enters <c>src/**</c> — this only downloads + verifies
/// + places the opaque artifact; the sidecar in <c>tools/</c> is the only thing
/// that ever reads it (keeps the #921 decoder-free-graph guard green).
/// </summary>
public sealed class ClassDataTpkProvisioner : IClassDataTpkProvisioner
{
    /// <summary>
    /// Canonical filename, shared with the engine/bootstrap so both agree on the
    /// path inside the asset-cache dir. Never hardcode the string elsewhere.
    /// </summary>
    public const string TpkFileName = "classdata.tpk";

    /// <summary>
    /// UABEA master <c>ReleaseFiles/classdata.tpk</c>. Third-party; downloaded at
    /// runtime to the user's machine only — never committed or staged (#921).
    /// </summary>
    public const string DownloadUrl =
        "https://github.com/nesrak1/UABEA/raw/master/ReleaseFiles/classdata.tpk";

    /// <summary>
    /// SHA-256 of the current UABEA master <c>ReleaseFiles/classdata.tpk</c>
    /// (verified by download during #960 implementation: 289,605 bytes, magic
    /// <c>TPK*</c>). Tracks the upstream master artifact; update this constant (and
    /// the size envelope below if it drifts materially) if UABEA revs the file.
    /// </summary>
    public const string ExpectedSha256 =
        "129e1f80f930415db6779fe6089afa75280cb51462bcee812beab6cd81a764c6";

    // Sane size envelope as a cheap pre-check before hashing. The real file is
    // ~283 KB; allow comfortable headroom so a minor upstream rev within the
    // pinned-SHA window still passes the floor, while an HTML redirect / error
    // page (tiny) or a wrong artifact (huge) is rejected up front.
    private const long DefaultMinSizeBytes = 200_000;   // ~200 KB floor
    private const long DefaultMaxSizeBytes = 5_000_000; // ~5 MB ceiling

    private readonly string _assetCacheDir;
    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly string _expectedSha;
    private readonly long _minSizeBytes;
    private readonly long _maxSizeBytes;

    public ClassDataTpkProvisioner(string assetCacheDir, HttpClient http, ILogger? logger = null)
        : this(assetCacheDir, http, ExpectedSha256, DefaultMinSizeBytes, DefaultMaxSizeBytes, logger)
    {
    }

    /// <summary>
    /// Test seam (<c>InternalsVisibleTo</c>): lets the unit tests drive the
    /// happy-path placement with a controllable expected SHA / size envelope
    /// without reproducing the real ~283 KB upstream bytes. Production always
    /// goes through the public ctor, which pins <see cref="ExpectedSha256"/>.
    /// </summary>
    internal ClassDataTpkProvisioner(
        string assetCacheDir, HttpClient http, string expectedSha,
        long minSizeBytes, long maxSizeBytes, ILogger? logger = null)
    {
        _assetCacheDir = assetCacheDir;
        _http = http;
        _expectedSha = expectedSha;
        _minSizeBytes = minSizeBytes;
        _maxSizeBytes = maxSizeBytes;
        _logger = logger;
    }

    /// <summary>The canonical on-disk path the sidecar's <c>--tpk</c> is pointed at.</summary>
    public string TpkPath => Path.Combine(_assetCacheDir, TpkFileName);

    public bool IsInstalled()
    {
        try
        {
            var path = TpkPath;
            if (!File.Exists(path)) return false;
            var len = new FileInfo(path).Length;
            return len >= _minSizeBytes && len <= _maxSizeBytes;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to stat classdata.tpk at {Path}", TpkPath);
            return false;
        }
    }

    public async Task<TpkProvisionResult> EnsureAsync(
        IProgress<TpkProvisionProgress>? progress, CancellationToken ct)
    {
        if (IsInstalled())
        {
            _logger?.LogInformation("classdata.tpk already present at {Path}; skipping download.", TpkPath);
            return new TpkProvisionResult(TpkProvisionStatus.AlreadyPresent, "Already installed.");
        }

        var dest = TpkPath;
        var tmp = dest + ".download-" + Guid.NewGuid().ToString("N") + ".partial";

        try
        {
            Directory.CreateDirectory(_assetCacheDir);

            _logger?.LogInformation("Downloading classdata.tpk from {Url}", DownloadUrl);

            using (var resp = await _http.GetAsync(
                DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();

                var total = resp.Content.Headers.ContentLength;
                using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var dst = new FileStream(
                    tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

                var buffer = new byte[64 * 1024];
                long received = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new TpkProvisionProgress(received, total));
                }
            }

            // ── Verify: size floor/ceiling, then SHA-256 against the pin. ──
            var size = new FileInfo(tmp).Length;
            if (size < _minSizeBytes || size > _maxSizeBytes)
            {
                _logger?.LogWarning(
                    "classdata.tpk download size {Size} bytes outside expected envelope [{Min}, {Max}]; rejecting.",
                    size, _minSizeBytes, _maxSizeBytes);
                return Fail(tmp, $"Downloaded file size ({size:N0} bytes) is outside the expected range.");
            }

            var actualSha = await ComputeSha256Async(tmp, ct).ConfigureAwait(false);
            if (!string.Equals(actualSha, _expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning(
                    "classdata.tpk SHA-256 {Actual} != expected {Expected}; rejecting download.",
                    actualSha, _expectedSha);
                return Fail(tmp, "Downloaded file failed integrity check (SHA-256 mismatch).");
            }

            // Atomic move into place (retries through AV/indexer flake).
            var bytes = await File.ReadAllBytesAsync(tmp, ct).ConfigureAwait(false);
            await AtomicFile.WriteAllBytesAtomicAsync(dest, bytes, ct).ConfigureAwait(false);
            TryDelete(tmp);

            _logger?.LogInformation("classdata.tpk installed at {Path} ({Size} bytes).", dest, size);
            return new TpkProvisionResult(TpkProvisionStatus.Downloaded, "Downloaded and verified.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(tmp);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "classdata.tpk download failed; map icons stay disabled (safe-degrade).");
            return Fail(tmp, $"Download failed: {ex.Message}");
        }
    }

    private TpkProvisionResult Fail(string tmp, string message)
    {
        TryDelete(tmp);
        return new TpkProvisionResult(TpkProvisionStatus.Failed, message);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Failed to delete temp tpk {Path}", path); }
    }
}
