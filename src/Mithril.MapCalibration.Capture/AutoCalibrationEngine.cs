using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Arda.World.Player;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Diagnostics.Telemetry;
using Mithril.Shared.Game;
using Mithril.Shared.MapCalibration;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// One auto-calibration attempt for the current area (spec §4): resolve area +
/// window + framed bbox, capture under the blanked overlay, refine the map
/// sub-rect, resolve the area's base texture + references, run the detect→solve→
/// gate engine, and persist the solved transform via
/// <see cref="IMapCalibrationService.SaveUserRefinement"/> (stamped
/// <see cref="CalibrationSource.AutoCapture"/>) ONLY when the gate accepts.
///
/// <para><b>Fail-soft everywhere</b> (spec §11): no current area / no bbox / PG
/// not foreground / bad capture / null base texture / low-confidence solve → keep
/// the prior calibration, return a reason for status surfacing, NEVER persist a
/// wrong transform.</para>
///
/// <para><b>Base-texture policy (Task 21 / Decision D8).</b> The base texture is
/// resolved from <see cref="IBaseTextureProvider"/> (the #931 seam over the
/// out-of-process sidecar cache — this layer writes NO provider code). On a
/// cache-miss (null) and when an <see cref="IAssetExtractor"/> is wired, invoke
/// the sidecar once to populate the cache, then retry the provider. If still
/// null, fail-soft with a "preparing map assets…" reason (no texture → no
/// detections → gate rejects → safe-degrade).</para>
///
/// <para><b>Icon-template policy (#949).</b> Icon templates resolve from
/// <see cref="IIconTemplateProvider"/> <i>per attempt</i> (it re-reads the cache
/// each call — the icon analogue of the base-texture provider). On an empty set and
/// when an <see cref="IAssetExtractor"/> is wired, the sidecar's <c>--icons</c> mode
/// is demand-triggered once to populate the cache, then the set is re-resolved — so
/// first-session calibration succeeds on a fresh icon cache without a restart. A
/// still-empty set fails soft: no typed detections → the gate rejects.</para>
/// </summary>
public sealed class AutoCalibrationEngine : IAutoCalibrationRunner
{
    // Proven Phase-1 detection recipe (§0): the gate-study sweet-spot for real
    // assets. RenderSizePx 16 is the empirical icon render size.
    private const int RenderSizePx = 16;
    private const double LowNcc = 0.5;
    private const double TypeFloor = 0.80;
    private static readonly BlobOptions BlobOpts = new(
        MinArea: 12, MaxIconArea: 900, MinSolidity: 0.35, MaxAspect: 2.5, MinPeak: 0.7);
    private const double RefineMinScore = 0.5;

    private readonly IAreaState _areaState;
    private readonly IGameWindowLocator _windowLocator;
    private readonly IMapCaptureRegionProvider _region;
    private readonly ICaptureService _capture;
    private readonly IMapRegionRefiner _refiner;
    private readonly IBaseTextureProvider _baseTextures;
    private readonly IAreaReferenceProvider _references;
    private readonly IMapCalibrationSolver _solver;
    private readonly IIconTemplateProvider _iconTemplates;
    private readonly IMapCalibrationService _calibrationService;
    private readonly ILogger? _logger;

    // Optional Task-21 sidecar policy (null in unit branch tests + when no
    // extractor is wired): on a base-texture cache miss, populate the cache then
    // retry. GameConfig supplies the PG install root for the extract request.
    private readonly IAssetExtractor? _assetExtractor;
    private readonly GameConfig? _gameConfig;
    private readonly string? _assetCacheDir;
    private readonly string? _pgVersion;

    public AutoCalibrationEngine(
        IAreaState areaState,
        IGameWindowLocator windowLocator,
        IMapCaptureRegionProvider region,
        ICaptureService capture,
        IMapRegionRefiner refiner,
        IBaseTextureProvider baseTextures,
        IAreaReferenceProvider references,
        IMapCalibrationSolver solver,
        IIconTemplateProvider iconTemplates,
        IMapCalibrationService calibrationService,
        ILogger? logger,
        IAssetExtractor? assetExtractor = null,
        GameConfig? gameConfig = null,
        string? assetCacheDir = null,
        string? pgVersion = null)
    {
        _areaState = areaState;
        _windowLocator = windowLocator;
        _region = region;
        _capture = capture;
        _refiner = refiner;
        _baseTextures = baseTextures;
        _references = references;
        _solver = solver;
        _iconTemplates = iconTemplates;
        _calibrationService = calibrationService;
        _logger = logger;
        _assetExtractor = assetExtractor;
        _gameConfig = gameConfig;
        _assetCacheDir = assetCacheDir;
        _pgVersion = pgVersion;
    }

    /// <summary>
    /// The downloaded <c>classdata.tpk</c> path inside the asset cache, or null when
    /// it isn't present yet (#960). Threaded into <see cref="ExtractRequest.TpkPath"/>
    /// so the sidecar can decode icons; when null the sidecar falls back to its old
    /// resolution and fail-softs exactly as before.
    /// </summary>
    private string? ResolveTpkPath()
    {
        if (string.IsNullOrWhiteSpace(_assetCacheDir)) return null;
        var tpk = Path.Combine(_assetCacheDir, ClassDataTpkProvisioner.TpkFileName);
        return File.Exists(tpk) ? tpk : null;
    }

    public async Task<AutoCalibrationOutcome> TryCalibrateCurrentAreaAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Per-attempt trace span (#914). Null when no listener is attached (no OTLP
        // export / no perf-recording), so this is zero-overhead when off. Child
        // capture/refine/solve spans nest under it → a Seq waterfall showing which
        // step is slow (the brute-force refine, until #966). Per-candidate refine
        // spans live deeper (MapRectLocator, in the Shared-free core) — deferred to #966.
        using var attempt = MithrilActivitySources.MapCalibration.StartActivity("calibration.attempt");

        var area = _areaState.CurrentArea;
        if (string.IsNullOrWhiteSpace(area))
        {
            return Fail("", "not in-world — open Project Gorgon and enter an area first");
        }

        // PG-foreground gate: capture must read the game's framebuffer, not
        // another app's. (The hotkey already focus-gates; the auto path + manual
        // path both re-check here so neither can capture the wrong window.)
        if (_windowLocator.Locate() is null)
        {
            return Fail(area, "Project Gorgon is not the foreground window");
        }

        var bbox = _region.Current;
        if (bbox is null)
        {
            return Fail(area, "no map bbox set — use the draw-map-bbox hotkey first");
        }

        attempt?.SetTag("map.area", area);
        _logger?.LogInformation(
            "Auto-calibration {Area}: capturing map region {Width}x{Height} at ({X},{Y})…",
            area, bbox.Value.Width, bbox.Value.Height, bbox.Value.X, bbox.Value.Y);
        GrayImage? gray;
        using (var captureAct = MithrilActivitySources.MapCalibration.StartActivity("calibration.capture"))
        {
            captureAct?.SetTag("bbox.width", bbox.Value.Width);
            captureAct?.SetTag("bbox.height", bbox.Value.Height);
            gray = await _capture.CaptureMapAsync(bbox.Value, ct).ConfigureAwait(false);
            captureAct?.SetTag("capture.ok", gray is not null);
        }
        if (gray is null)
        {
            return Fail(area, "map capture failed or was rejected (black / wrong-size frame)");
        }

        _logger?.LogInformation(
            "Auto-calibration {Area}: captured {Width}x{Height} frame; resolving base texture…",
            area, gray.Width, gray.Height);
        var baseTexture = await ResolveBaseTextureAsync(area, ct).ConfigureAwait(false);
        if (baseTexture is null)
        {
            return Fail(area, "preparing map assets… (base texture unavailable — no detections possible)");
        }

        // Texture-registration refine — a synchronous NCC pass over the base
        // texture that can take a noticeable moment on a cold call. Bracket it
        // with before/after + timing so a slow or stalled refine is visible (the
        // attempt previously went dark after "Loaded base texture …").
        _logger?.LogInformation(
            "Auto-calibration {Area}: locating the map within the captured frame (texture registration)…", area);
        var refineStart = Stopwatch.GetTimestamp();
        MapRect? mapRect;
        using (var refineAct = MithrilActivitySources.MapCalibration.StartActivity("calibration.refine"))
        {
            mapRect = _refiner.Refine(gray, baseTexture, RefineMinScore);
            refineAct?.SetTag("map.located", mapRect is not null);
        }
        if (mapRect is null)
        {
            return Fail(area, "couldn't locate the map in the captured frame — zoom the in-game map all the way out");
        }
        _logger?.LogInformation(
            "Auto-calibration {Area}: map sub-rect located ({MapRect}) in {ElapsedMs:0} ms.",
            area, mapRect, Stopwatch.GetElapsedTime(refineStart).TotalMilliseconds);

        var references = _references.ForArea(area);
        _logger?.LogInformation(
            "Auto-calibration {Area}: {ReferenceCount} landmark reference(s) for this area.", area, references.Count);

        // Resolve icon templates per attempt (#949). On a fresh icon cache the
        // provider returns Empty; if a sidecar is wired, demand-trigger its --icons
        // mode ONCE to populate the cache, then re-resolve — so first-session
        // calibration works without a restart. Fail-soft: still-Empty → no typed
        // detections → the gate rejects → safe-degrade.
        var templates = await EnsureIconTemplatesAsync(ct).ConfigureAwait(false);

        var request = new DetectionRequest(
            Screenshot: gray,
            BaseTexture: baseTexture,
            MapRect: mapRect,
            Templates: templates,
            RimMask: RimMaskMode.DeviationFlood,
            LowNcc: LowNcc,
            TypeFloor: TypeFloor,
            BlobOptions: BlobOpts)
        {
            RenderSizePx = RenderSizePx,
        };

        _logger?.LogInformation(
            "Auto-calibration {Area}: running detect→solve ({TemplateCount} icon template(s), {ReferenceCount} reference(s))…",
            area, templates.Templates.Count, references.Count);
        var solveStart = Stopwatch.GetTimestamp();
        CalibrationSolveResult result;
        using (var solveAct = MithrilActivitySources.MapCalibration.StartActivity("calibration.solve"))
        {
            solveAct?.SetTag("templates", templates.Templates.Count);
            solveAct?.SetTag("references", references.Count);
            result = _solver.Solve(request, references);
            solveAct?.SetTag("solve.inliers", result.InlierCount);
            solveAct?.SetTag("solve.calibrated", result.Calibration is not null);
            if (result.Calibration is not null)
            {
                solveAct?.SetTag("solve.residual_px", result.Calibration.ResidualPixels);
            }
        }
        _logger?.LogInformation(
            "Auto-calibration {Area}: solve finished in {ElapsedMs:0} ms (calibration {HasCalibration}, {Inliers} inlier(s)).",
            area, Stopwatch.GetElapsedTime(solveStart).TotalMilliseconds, result.Calibration is not null, result.InlierCount);
        if (result.Calibration is null)
        {
            var reason = result.RejectReason ?? "no geometrically-consistent fit";
            _logger?.LogInformation("Auto-calibration rejected for {Area}: {Reason}. Prior calibration kept.", area, reason);
            return new AutoCalibrationOutcome(Persisted: false, AreaKey: area, RejectReason: reason);
        }

        // Gate-accept: persist through the user store stamped AutoCapture, which
        // inherits user-store precedence by construction (Task 20).
        var stamped = result.Calibration with { Source = CalibrationSource.AutoCapture };
        _calibrationService.SaveUserRefinement(area, stamped);
        _logger?.LogInformation(
            "Auto-calibration persisted for {Area} (residual {Residual:0.00} px, {Inliers} inliers).",
            area, stamped.ResidualPixels, result.InlierCount);
        return new AutoCalibrationOutcome(Persisted: true, AreaKey: area, RejectReason: null);
    }

    /// <summary>
    /// Task-21 policy. Resolve the base texture from the #931 provider; on a
    /// cache-miss, optionally trigger the sidecar once to populate the cache,
    /// then retry. Fail-soft to null on any path.
    /// </summary>
    private async Task<GrayImage?> ResolveBaseTextureAsync(string area, CancellationToken ct)
    {
        var tex = _baseTextures.TryGetBaseTexture(area);
        if (tex is not null) return tex;

        if (_assetExtractor is null || _gameConfig is null
            || string.IsNullOrWhiteSpace(_gameConfig.InstallRoot) || string.IsNullOrWhiteSpace(_assetCacheDir))
        {
            return null; // no extractor wired → safe-degrade (caller surfaces "preparing map assets…")
        }

        _logger?.LogInformation("Base texture cache-miss for {Area}; invoking asset-extractor sidecar.", area);
        try
        {
            var request = new ExtractRequest(
                InstallRoot: _gameConfig.InstallRoot,
                OutDir: _assetCacheDir!,
                Kind: ExtractKind.Texture,
                AreaKey: area,
                ExpectPgVersion: _pgVersion,
                TpkPath: ResolveTpkPath());
            var extract = await _assetExtractor.ExtractAsync(request, ct).ConfigureAwait(false);
            if (!extract.Ok)
            {
                _logger?.LogWarning(
                    "Asset-extractor sidecar failed for {Area} (exit {Exit}): {Error}. Safe-degrade.",
                    area, extract.ExitCode, extract.Error);
                return null;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Asset-extractor sidecar threw for {Area}. Safe-degrade.", area);
            return null;
        }

        var retried = _baseTextures.TryGetBaseTexture(area); // retry after populate
        if (retried is null)
        {
            // The extractor reported success but the provider still has no usable
            // texture for this area. Distinguish this from a plain transient
            // cache-miss: it usually means an asset-shape change or a
            // canonical-hash-gate mismatch (the extracted bytes don't match the
            // gated hash), which a future PG patch can introduce silently.
            // Behaviour is unchanged (still fail-soft); this just makes the
            // gate/shape mismatch visible instead of looking like a cache hiccup.
            _logger?.LogWarning(
                "Asset-extractor reported success for {Area} but no usable base texture is available after retry "
                + "(possible asset-shape change or canonical-hash-gate mismatch, not a transient cache-miss). Safe-degrade.",
                area);
        }
        return retried;
    }

    /// <summary>
    /// #949 policy (icon analogue of <see cref="ResolveBaseTextureAsync"/>). Resolve
    /// the icon-template set from the per-attempt <see cref="IIconTemplateProvider"/>;
    /// on an empty set, optionally demand-trigger the sidecar's <c>--icons</c> mode
    /// once to populate the cache, then re-resolve. Fail-soft to whatever the
    /// provider returns (Empty included) on any path — never throws into the engine.
    /// </summary>
    private async Task<IconTemplateSet> EnsureIconTemplatesAsync(CancellationToken ct)
    {
        var templates = _iconTemplates.GetTemplates();
        if (templates.Templates.Count > 0) return templates;

        if (_assetExtractor is null || _gameConfig is null
            || string.IsNullOrWhiteSpace(_gameConfig.InstallRoot) || string.IsNullOrWhiteSpace(_assetCacheDir))
        {
            return templates; // no extractor wired → safe-degrade (Empty → gate rejects)
        }

        _logger?.LogInformation("Icon-template cache empty; invoking asset-extractor sidecar (--icons) on demand.");
        try
        {
            var request = new ExtractRequest(
                InstallRoot: _gameConfig.InstallRoot,
                OutDir: _assetCacheDir!,
                Kind: ExtractKind.Icons,
                AreaKey: null,
                ExpectPgVersion: _pgVersion,
                TpkPath: ResolveTpkPath());
            var extract = await _assetExtractor.ExtractAsync(request, ct).ConfigureAwait(false);
            if (!extract.Ok)
            {
                _logger?.LogWarning(
                    "Asset-extractor sidecar (--icons) failed (exit {Exit}): {Error}. Safe-degrade (no icon detections).",
                    extract.ExitCode, extract.Error);
                return templates;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Asset-extractor sidecar (--icons) threw. Safe-degrade (no icon detections).");
            return templates;
        }

        return _iconTemplates.GetTemplates(); // re-resolve after populate
    }

    private AutoCalibrationOutcome Fail(string area, string reason)
    {
        _logger?.LogInformation("Auto-calibration not attempted for {Area}: {Reason}.", string.IsNullOrEmpty(area) ? "<none>" : area, reason);
        return new AutoCalibrationOutcome(Persisted: false, AreaKey: area, RejectReason: reason);
    }
}

/// <summary>
/// The outcome of one auto-calibration attempt: whether a transform was
/// persisted, the area it was for, and (when not persisted) a user-facing reason
/// for status surfacing (<see cref="CalibrationStatusFormatter"/>).
/// </summary>
public sealed record AutoCalibrationOutcome(bool Persisted, string AreaKey, string? RejectReason);
