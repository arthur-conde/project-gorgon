using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.DependencyInjection;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Game;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Settings;

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
    /// Wire the auto-capture pipeline. <paramref name="settingsDir"/> holds the
    /// persisted capture bbox; <paramref name="assetCacheDir"/> is the
    /// out-of-process asset-extractor sidecar cache the #931 base-texture +
    /// icon-template loaders read (BCL-only). Requires the shell to have already
    /// registered the cross-cutting singletons it consumes: <see cref="GameConfig"/>,
    /// <see cref="Mithril.Overlay.IOverlayWindow"/>,
    /// <see cref="Arda.Contracts.IDomainEventSubscriber"/>,
    /// <see cref="Arda.World.Player.IAreaState"/>, and
    /// <see cref="Mithril.Shared.Reference.IReferenceDataService"/>.
    /// </summary>
    public static IServiceCollection AddMithrilMapCalibrationCapture(
        this IServiceCollection services,
        string settingsDir,
        string assetCacheDir,
        string? pgVersion = null)
    {
        if (string.IsNullOrWhiteSpace(settingsDir))
            throw new System.ArgumentException("settingsDir required", nameof(settingsDir));
        if (string.IsNullOrWhiteSpace(assetCacheDir))
            throw new System.ArgumentException("assetCacheDir required", nameof(assetCacheDir));

        Directory.CreateDirectory(settingsDir);

        // Phase-1 detect→solve engine + IconTemplateSet + IBaseTextureProvider
        // (the #931 sidecar-cache seam) over the asset cache. This also registers
        // the engine's DEFAULT ICalibrationConfidenceGate; we override it below.
        services.AddMithrilMapCalibrationEngine(assetCacheDir, pgVersion);

        // GameConfig-wired gate override (Task 23). Registered AFTER the engine so
        // last-registration-wins makes this the resolved ICalibrationConfidenceGate
        // (honours the user-tunable GameConfig.CalibrationGoodResidualPx).
        services.AddSingleton<ICalibrationConfidenceGate>(sp =>
            BuildConfidenceGate(sp.GetRequiredService<GameConfig>()));

        // Persisted capture bbox.
        services.AddSingleton<ISettingsStore<MapCaptureSettings>>(_ =>
            new JsonSettingsStore<MapCaptureSettings>(
                Path.Combine(settingsDir, "map-capture.json"),
                MapCaptureSettingsJsonContext.Default.MapCaptureSettings));
        services.AddSingleton<IMapCaptureRegionProvider>(sp =>
        {
            var store = sp.GetRequiredService<ISettingsStore<MapCaptureSettings>>();
            return new MapCaptureRegionProvider(store, store.Load());
        });

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
            sp.GetRequiredService<IMapCaptureRegionProvider>(),
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
