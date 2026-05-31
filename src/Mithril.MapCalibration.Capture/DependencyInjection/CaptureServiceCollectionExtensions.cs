using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.DependencyInjection;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Game;
using Mithril.Shared.Hotkeys;

namespace Mithril.MapCalibration.Capture.DependencyInjection;

/// <summary>
/// DI composition for the map auto-capture pipeline (mithril#914 PR-2). Registers
/// the OS-capture seams, the headless detect→solve engine (Phase 1, via
/// <see cref="MapCalibrationServiceCollectionExtensions.AddMithrilMapCalibrationEngine"/>),
/// the orchestrator, the two hotkeys, and the background trigger.
/// </summary>
public static partial class CaptureServiceCollectionExtensions
{
    /// <summary>
    /// File name of the out-of-process asset-extractor sidecar (issue #931), as
    /// produced by <c>tools/Mithril.AssetExtractor</c>'s
    /// <c>&lt;AssemblyName&gt;mithril-asset-extract&lt;/AssemblyName&gt;</c> and
    /// published next to <c>Mithril.exe</c> in both pack variants
    /// (<c>release.yml</c> "Publish asset-extractor sidecar"). Resolved at runtime
    /// relative to <see cref="AppContext.BaseDirectory"/> (the install dir holding
    /// the shell). Exposed so the wiring test can assert the same path the
    /// registration builds.
    /// </summary>
    public const string AssetExtractorExeName = "mithril-asset-extract.exe";

    /// <summary>
    /// Hard upper bound on a single sidecar run. The sidecar decodes one area's
    /// base texture or the full icon set from the on-disk PG bundles; on a healthy
    /// install both complete in a couple of seconds, but a cold disk / large bundle
    /// / contended IO can stretch that. 60s is generous headroom that still kills a
    /// genuinely hung child (the timeout fail-softs to "no texture" → gate rejects).
    /// </summary>
    private static readonly TimeSpan AssetExtractorTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Wire the auto-capture pipeline. <paramref name="assetCacheDir"/> is the
    /// out-of-process asset-extractor sidecar cache the #931 base-texture +
    /// icon-template loaders read (BCL-only). Requires the shell to have already
    /// registered the cross-cutting singletons it consumes: <see cref="GameConfig"/>,
    /// <see cref="Mithril.Overlay.IOverlayWindow"/>,
    /// <see cref="Arda.Contracts.IDomainEventSubscriber"/>,
    /// <see cref="Arda.World.Player.IAreaState"/>, and
    /// <see cref="Mithril.Shared.Reference.IReferenceDataService"/>.
    ///
    /// <para>#940: the capture region is the live overlay-window bounds (one-rect
    /// model, spec §7); there is no separately-persisted capture rect, so this no
    /// longer takes a <c>settingsDir</c> — the overlay's own
    /// <c>WindowLayoutBinder</c> persists its bounds to <c>LegolasSettings</c>.</para>
    /// </summary>
    public static IServiceCollection AddMithrilMapCalibrationCapture(
        this IServiceCollection services,
        string assetCacheDir,
        string? pgVersion = null)
    {
        if (string.IsNullOrWhiteSpace(assetCacheDir))
            throw new System.ArgumentException("assetCacheDir required", nameof(assetCacheDir));

        // Phase-1 detect→solve engine + IconTemplateSet + IBaseTextureProvider
        // (the #931 sidecar-cache seam) over the asset cache. This also registers
        // the engine's DEFAULT ICalibrationConfidenceGate; we override it below.
        services.AddMithrilMapCalibrationEngine(assetCacheDir, pgVersion);

        // GameConfig-wired gate override (Task 23). Registered AFTER the engine so
        // last-registration-wins makes this the resolved ICalibrationConfidenceGate
        // (honours the user-tunable GameConfig.CalibrationGoodResidualPx).
        services.AddSingleton<ICalibrationConfidenceGate>(sp =>
            BuildConfidenceGate(sp.GetRequiredService<GameConfig>()));

        // Capture region = live overlay-window bounds (#940 one-rect model). No
        // separate persisted rect: the overlay's own WindowLayoutBinder persists
        // its bounds to LegolasSettings.MapOverlay.
        services.AddSingleton<IMapCaptureRegionProvider>(sp =>
            new MapCaptureRegionProvider(
                sp.GetRequiredService<Mithril.Overlay.IOverlayWindow>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Region")));

        // OS capture seams.
        services.AddSingleton<IGameWindowLocator>(sp => new Win32GameWindowLocator(
            sp.GetRequiredService<GameConfig>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Window")));
        services.AddSingleton<IScreenCapture>(sp => new BitBltScreenCapture(
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Screen")));
        services.AddSingleton<IMapRegionRefiner, TextureRegistrationRefiner>();
        services.AddSingleton<CaptureValidation>();

        // Overlay blanking + the capture orchestration over it.
        services.AddSingleton<IOverlayBlanker, OverlayBlanker>();
        services.AddSingleton<ICaptureService>(sp => new CaptureService(
            sp.GetRequiredService<IScreenCapture>(),
            sp.GetRequiredService<IOverlayBlanker>(),
            sp.GetRequiredService<CaptureValidation>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Service")));

        // Reference points + the solve seam.
        services.AddSingleton<IAreaReferenceProvider>(sp => new ReferenceDataAreaReferenceProvider(
            sp.GetRequiredService<Mithril.Shared.Reference.IReferenceDataService>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.References")));
        services.AddSingleton<IMapCalibrationSolver, MapCalibrationSolveEngineAdapter>();

        // #945 Gap 1: the out-of-process asset-extractor sidecar adapter the engine
        // invokes on a base-texture cache-miss (AutoCalibrationEngine resolves this
        // via the optional sp.GetService<IAssetExtractor>() below — it returned null
        // until this registration existed, so the cache never populated). Registered
        // BEFORE the engine for logical ordering (DI lambdas are lazy, so strict
        // order isn't required for correctness). BCL-only (System.Diagnostics.Process)
        // — the decoder stays out-of-process (#921), the only link is the process
        // boundary. Registered UNCONDITIONALLY (no File.Exists gate): when the exe is
        // absent (dev/F5), ProcessAssetExtractor.ExtractAsync fail-softs with
        // ExitMissingExe rather than throwing, so the graph stays deterministic.
        services.AddSingleton<IAssetExtractor>(sp => new ProcessAssetExtractor(
            exePath: Path.Combine(AppContext.BaseDirectory, AssetExtractorExeName),
            timeout: AssetExtractorTimeout,
            logger: sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.AssetExtractor")));

        // The orchestrator. Resolved as both the concrete type (for the hotkey +
        // DI test) and the narrow IAutoCalibrationRunner seam.
        services.AddSingleton<AutoCalibrationEngine>(sp => new AutoCalibrationEngine(
            sp.GetRequiredService<Arda.World.Player.IAreaState>(),
            sp.GetRequiredService<IGameWindowLocator>(),
            sp.GetRequiredService<IMapCaptureRegionProvider>(),
            sp.GetRequiredService<ICaptureService>(),
            sp.GetRequiredService<IMapRegionRefiner>(),
            sp.GetRequiredService<IBaseTextureProvider>(),
            sp.GetRequiredService<IAreaReferenceProvider>(),
            sp.GetRequiredService<IMapCalibrationSolver>(),
            sp.GetRequiredService<IconTemplateSet>(),
            sp.GetRequiredService<IMapCalibrationService>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Engine"),
            assetExtractor: sp.GetService<IAssetExtractor>(),
            gameConfig: sp.GetRequiredService<GameConfig>(),
            assetCacheDir: assetCacheDir,
            pgVersion: pgVersion));
        services.AddSingleton<IAutoCalibrationRunner>(sp => sp.GetRequiredService<AutoCalibrationEngine>());

        // Bbox draw controller (shell-side, over IOverlayWindow).
        services.AddSingleton<IMapBboxDrawController>(sp => new MapBboxDrawController(
            sp.GetRequiredService<Mithril.Overlay.IOverlayWindow>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.Capture.Draw")));

        // Hotkeys.
        services.AddSingleton<IHotkeyCommand>(sp =>
            new Hotkeys.CaptureCalibrateCommand(
                sp.GetRequiredService<IAutoCalibrationRunner>(),
                sp.GetRequiredService<Mithril.Overlay.IOverlayWindow>()));
        services.AddSingleton<IHotkeyCommand>(sp =>
            new Hotkeys.DrawMapBboxCommand(sp.GetRequiredService<IMapBboxDrawController>()));

        // Background auto-attempt trigger. A hosted service → ILogger is required
        // (CLAUDE.md): resolve ILoggerFactory as required and create the category
        // logger rather than threading an optional logger.
        services.AddSingleton<AutoCalibrationTrigger>(sp => new AutoCalibrationTrigger(
            sp.GetRequiredService<Arda.Contracts.IDomainEventSubscriber>(),
            sp.GetRequiredService<IAutoCalibrationRunner>(),
            sp.GetRequiredService<IMapCaptureRegionProvider>(),
            sp.GetRequiredService<IGameWindowLocator>(),
            sp.GetRequiredService<IMapCalibrationService>(),
            sp.GetRequiredService<Mithril.Overlay.IOverlayWindow>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Mithril.MapCalibration.Capture.Trigger")));
        services.AddHostedService(sp => sp.GetRequiredService<AutoCalibrationTrigger>());

        // #945 Gap 3: one-time icon-template cache bootstrap. The engine's cache-miss
        // path only requests per-area textures, so nothing ever runs the sidecar's
        // --icons mode and IconTemplateSet stays Empty. This hosted service runs
        // --icons once on startup when the icon cache isn't yet populated. NEXT-LAUNCH
        // semantics by design: IconTemplateSet is resolved once at startup (when the
        // hosted services are constructed), so the populated cache takes in-memory
        // effect on the SUBSEQUENT launch — see IconTemplateBootstrap's class doc.
        // Fail-soft: no exe / GameRoot empty / non-zero exit → no icons, no throw.
        services.AddSingleton<IconTemplateBootstrap>(sp => new IconTemplateBootstrap(
            sp.GetRequiredService<IAssetExtractor>(),
            sp.GetRequiredService<GameConfig>(),
            assetCacheDir,
            pgVersion,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Mithril.MapCalibration.Capture.IconBootstrap")));
        services.AddHostedService(sp => sp.GetRequiredService<IconTemplateBootstrap>());

        return services;
    }

    /// <summary>
    /// Build the auto path's <see cref="ICalibrationConfidenceGate"/> honouring
    /// <see cref="GameConfig.CalibrationGoodResidualPx"/> (the SAME user-tunable
    /// threshold the manual path uses — PR-0 relocated it to GameConfig; spec §9),
    /// with the shipped <see cref="CalibrationConfidenceGate.DefaultInlierFloor"/>.
    /// </summary>
    internal static ICalibrationConfidenceGate BuildConfidenceGate(GameConfig cfg) =>
        new CalibrationConfidenceGate(cfg.CalibrationGoodResidualPx, CalibrationConfidenceGate.DefaultInlierFloor);
}
