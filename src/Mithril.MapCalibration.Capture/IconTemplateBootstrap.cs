using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Game;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// #945 Gap 3 — one-time icon-template cache bootstrap. The engine's
/// base-texture cache-miss path only ever requests <see cref="ExtractKind.Texture"/>
/// (per-area); nothing requests <see cref="ExtractKind.Icons"/>, so the icon cache
/// stays empty and <see cref="IconTemplateSet"/> loads as
/// <see cref="IconTemplateSet.Empty"/> forever — icon detections never happen. This
/// hosted service runs the sidecar's <c>--icons</c> mode once, on startup, when the
/// icon cache is not yet populated, so the manifest+blob land on disk.
///
/// <para><b>Trigger semantics — NEXT-LAUNCH (option b, by design).</b>
/// <see cref="IconTemplateSet"/> is resolved <i>once, eagerly</i> by the
/// <c>AddSingleton&lt;IconTemplateSet&gt;</c> lambda in
/// <c>AddMithrilMapCalibrationEngine</c> (via
/// <c>BundledIconTemplateLoader.LoadFromDirectory</c>). All hosted services —
/// including this one and <see cref="AutoCalibrationTrigger"/> — are <i>constructed</i>
/// when the host materialises the <c>IEnumerable&lt;IHostedService&gt;</c> at
/// startup, and constructing <see cref="AutoCalibrationTrigger"/> resolves
/// <c>IAutoCalibrationRunner</c> → <see cref="AutoCalibrationEngine"/> →
/// <see cref="IconTemplateSet"/>. So by the time this service's
/// <see cref="StartAsync"/> can finish populating the cache, the
/// <see cref="IconTemplateSet"/> singleton may already have been resolved as
/// <see cref="IconTemplateSet.Empty"/>. We therefore deliberately do NOT try to
/// guarantee same-session in-memory effect (that would require a fragile
/// construction-ordering assumption or a reload-aware template holder). Instead:
/// <b>this run populates the cache; the next app launch loads the populated cache
/// into <see cref="IconTemplateSet"/>.</b> Acceptance "icon templates populate via
/// the --icons trigger" is satisfied at the <i>cache</i> level on this run, with the
/// in-memory icon-detection effect taking hold on the subsequent launch.</para>
///
/// <para><b>Fire-and-forget StartAsync.</b> The extraction runs on a background task
/// so it never blocks host startup (the sidecar can take seconds on a cold disk).
/// Failure is irrelevant to startup — it's a cache warm-up.</para>
///
/// <para><b>Fail-soft (preserved end-to-end).</b> No exe / empty
/// <see cref="GameConfig.GameRoot"/> / non-zero exit / timeout / crash → no icons,
/// no throw, no crash. The bootstrap is skipped entirely when the cache is already
/// populated (manifest present) so it runs at most once per fresh cache.</para>
/// </summary>
public sealed class IconTemplateBootstrap : IHostedService, IDisposable
{
    private readonly IAssetExtractor _extractor;
    private readonly GameConfig _gameConfig;
    private readonly string _assetCacheDir;
    private readonly string? _pgVersion;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    private Task? _bootstrap;

    public IconTemplateBootstrap(
        IAssetExtractor extractor,
        GameConfig gameConfig,
        string assetCacheDir,
        string? pgVersion,
        ILogger logger)
    {
        _extractor = extractor;
        _gameConfig = gameConfig;
        _assetCacheDir = assetCacheDir;
        _pgVersion = pgVersion;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget: warming the icon cache must not block host startup. Run
        // under a private CTS (not the startup token, which signals startup-abort and
        // doesn't fire on shutdown) so StopAsync can cancel an in-flight extraction.
        _bootstrap = Task.Run(() => RunOnceAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Best-effort: cancel an in-flight extraction and wait briefly for it to
        // unwind so we don't leave a dangling child past host shutdown. Any fault /
        // cancellation is swallowed — RunOnceAsync is already fail-soft.
        _cts.Cancel();
        if (_bootstrap is { } b)
        {
            try { await b.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch { /* cancelled / faulted / shutdown-token tripped — nothing to do */ }
        }
    }

    /// <summary>
    /// The bootstrap decision + extraction, extracted for unit testing. Decides
    /// whether to invoke the sidecar's <c>--icons</c> mode and, if so, runs it
    /// fail-soft. Returns <c>true</c> iff the sidecar was invoked (regardless of
    /// the sidecar's own success/failure), <c>false</c> when skipped by a gate.
    /// </summary>
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_gameConfig.GameRoot))
        {
            _logger.LogInformation(
                "Icon-template bootstrap skipped: PG install root not configured (GameConfig.GameRoot empty). Safe-degrade.");
            return false;
        }

        // Already populated? The manifest's presence is the populated-cache signal
        // (IconTemplateCache reads the sidecar's icon-templates.json). Re-running the
        // sidecar on every launch would be wasted IO + a pointless child process.
        if (IconTemplateCache.IsPopulated(_assetCacheDir, _logger))
        {
            _logger.LogInformation(
                "Icon-template cache already populated at {CacheDir}; skipping --icons bootstrap.", _assetCacheDir);
            return false;
        }

        _logger.LogInformation(
            "Icon-template cache empty at {CacheDir}; invoking asset-extractor sidecar (--icons). "
            + "Icons take effect on the next app launch (the IconTemplateSet singleton is loaded once at startup).",
            _assetCacheDir);

        try
        {
            var request = new ExtractRequest(
                InstallRoot: _gameConfig.GameRoot,
                OutDir: _assetCacheDir,
                Kind: ExtractKind.Icons,
                AreaKey: null,
                ExpectPgVersion: _pgVersion);
            var result = await _extractor.ExtractAsync(request, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                _logger.LogInformation(
                    "Icon-template bootstrap populated the cache ({Count} artifact(s)); icons engage on next launch.",
                    result.Artifacts.Count);
            }
            else
            {
                _logger.LogWarning(
                    "Icon-template bootstrap: sidecar failed (exit {Exit}): {Error}. Icons stay disabled (safe-degrade).",
                    result.ExitCode, result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Icon-template bootstrap cancelled (host shutdown).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Icon-template bootstrap threw; icons stay disabled (safe-degrade).");
        }

        return true;
    }

    public void Dispose() => _cts.Dispose();
}
