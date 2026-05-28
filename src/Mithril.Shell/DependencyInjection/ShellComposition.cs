using System.Collections.Frozen;
using System.IO;
using Arda.Abstractions.Diagnostics;
using Arda.Composition;
using Arda.Contracts.State.Health;
using Arda.Hosting;
using Arda.Wpf;
using Arda.World.Chat;
using Arda.World.Player;
using Mithril.Shared.Audio;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Diagnostics.Performance;
using Mithril.Shared.Diagnostics.Telemetry;
using Mithril.Shared.Game;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Icons;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Mithril.Shared.Telemetry.Abstractions;
using Mithril.Shared.Telemetry.Hosting;
using Mithril.Shared.Telemetry.Logs;
using Mithril.Shared.Telemetry.Settings;
using Mithril.Shared.Wpf;
using Mithril.Shared.Wpf.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mithril.Shell.DependencyInjection;

/// <summary>
/// Inputs the shell service graph needs that are computed by the bootstrap before
/// the host is built (settings already loaded, cache directories resolved). Carried
/// as a record so the <em>one</em> composition is shared verbatim between the real
/// launch path (<c>Program</c>) and the headless self-check (<c>--selfcheck</c> /
/// the DI-cycle guard test) — no second hand-maintained copy of the registration
/// chain to drift out of sync (#365, Layer 2).
/// </summary>
public sealed record ShellCompositionOptions(
    string PreferencesPath,
    ISettingsStore<ShellSettings> ShellStore,
    ShellSettings ShellSettings,
    GameConfig GameConfig,
    string LogDir,
    string PerfDir,
    string CharactersRootDir,
    string ReferenceCacheDir,
    string CommunityCalibrationCacheDir,
    string IconCacheDir,
    string ShellSettingsDir,
    Action<string>? ModuleLog = null);

public static class ShellComposition
{
    /// <summary>
    /// Top-level composition entry point. Wires the full shell service graph.
    /// The Arda pipeline (L0–L3 + L4 composition) is the sole log-processing
    /// engine; the legacy world-sim merger was retired alongside
    /// <c>Mithril.GameState</c> and <c>Mithril.WorldSim.*</c>.
    /// </summary>
    public static IServiceCollection AddMithrilApp(
        this IServiceCollection services, ShellCompositionOptions o) =>
        services.AddMithrilShell(o);

    /// <summary>
    /// The complete shell service registration, in order. This is the single source
    /// of truth for the runtime DI graph; <c>Program</c> and the self-check both call
    /// it via <see cref="AddMithrilApp"/> so the guard validates exactly what ships.
    /// </summary>
    public static IServiceCollection AddMithrilShell(
        this IServiceCollection services, ShellCompositionOptions o)
    {
        services
            .AddMithrilSettings<UserPreferences>(o.PreferencesPath, UserPreferencesJsonContext.Default.UserPreferences)
            .AddSingleton<ISettingsStore<ShellSettings>>(o.ShellStore)
            .AddSingleton(o.ShellSettings)
            .AddSingleton<IActiveCharacterPersistence>(o.ShellSettings)
            .AddSingleton(o.GameConfig)
            // Trace-context enricher stamps trace_id / span_id onto on-disk diagnostics
            // log entries written inside an Activity scope (e.g. perf-recorder sessions,
            // OTLP-exported spans). Allocation-free no-op outside an active Activity, so
            // wiring it unconditionally is safe.
            .AddMithrilLogging(o.LogDir, new MithrilTraceContextEnricher())
            .AddMithrilPerfRecorder(o.PerfDir, sp => () => sp.GetRequiredService<ShellSettings>().VerboseFrameEvents)
            .AddMithrilGameServices()
            .AddMithrilPerCharacterStorage(o.CharactersRootDir)
            .AddMithrilReferenceData(o.ReferenceCacheDir)
            .AddMithrilCommunityCalibration(o.CommunityCalibrationCacheDir)
            .AddMithrilIcons(o.IconCacheDir)
            .AddMithrilAudio()
            .AddMithrilHotkeys()
            .AddMithrilDialogs()
            .AddMithrilModuleGates()
            // Register NoOp navigator BEFORE modules so module Register() calls
            // (which run inside AddMithrilModules) can override via AddSingleton.
            // Without this ordering, the NoOp would win and CanOpen would always
            // return false — chip cross-links would render disabled.
            .AddSingleton<IReferenceNavigator, NoOpReferenceNavigator>()
            .AddMithrilModules(o.ModuleLog)
            .AddMithrilAttention()
            .AddMithrilShellUpdates()
            .AddMithrilShellViews()
            .AddMithrilItemDetail()
            .AddMithrilIngredientSources()
            .AddMithrilShellCommands();

        // Opt-in OTLP export (mithril#815). Registration order:
        //   1. AddMithrilVersionedSettings<TelemetrySettings> — registers the
        //      JSON store, loads + migrates the singleton, wires SettingsAutoSaver.
        //   2. Tag-descriptor providers — TagCatalog resolves
        //      IEnumerable<ITagDescriptorProvider> at construction; without these
        //      the catalog is empty and every tag is treated as "newly-seen".
        //   3. AddMithrilOtlpExport(settings, loggerFactory) — no-op when
        //      EnableOtlpExport=false (the default), so end users see no behaviour
        //      change. Must receive the SAME TelemetrySettings instance the
        //      versioned-settings registration produced so settings-UI mutations
        //      to TagExports flow through to the scrubber per span without
        //      restart (see SingletonOptionsMonitor in TelemetryHostExtensions).
        services.AddMithrilVersionedSettings<TelemetrySettings>(
            Path.Combine(o.ShellSettingsDir, "telemetry.json"),
            TelemetrySettingsJsonContext.Default.TelemetrySettings);
        services.AddSingleton<ITagDescriptorProvider, MithrilSharedTagDescriptors>();
        services.AddSingleton<ITagDescriptorProvider, ArdaTagDescriptors>();

        // Resolve the loaded TelemetrySettings + ILoggerFactory to gate the
        // OTel pipeline at registration time. The temporary provider is scoped
        // to this using block; the real singletons live in the actual host
        // provider built later. Settings load is a one-time startup cost so
        // the extra resolve here is acceptable, and EnableOtlpExport changes
        // require a restart anyway (per TelemetrySettings XML doc).
        using (var tmp = services.BuildServiceProvider())
        {
            var telemetrySettings = tmp.GetRequiredService<TelemetrySettings>();
            var loggerFactory = tmp.GetRequiredService<ILoggerFactory>();
            services.AddMithrilOtlpExport(telemetrySettings, loggerFactory);
        }

        // L4 composition (singleton factories resolved eagerly by the bootstrap
        // below). Registered before the Arda drivers so hosted-service startup
        // order ensures all event subscriptions are wired before replay begins.
        services.AddArdaComposition(
            o.CharactersRootDir,
            recipeKeyResolverFactory: sp =>
            {
                var refData = sp.GetRequiredService<IReferenceDataService>();
                return id =>
                {
                    var key = $"recipe_{id}";
                    return refData.Recipes.TryGetValue(key, out var recipe)
                        ? recipe.InternalName ?? key
                        : key;
                };
            },
            serverFallbackFactory: sp =>
            {
                var active = sp.GetRequiredService<IActiveCharacterService>();
                return () => active.ActiveServer;
            });

        services.AddHostedService<CompositionBootstrap>();

        // Arda pipeline (L0–L3): the sole log-processing engine.
        // Uses the game root as log directory (Player.log + ChatLogs/).
        services
            .AddArda(new ArdaOptions(o.GameConfig.GameRoot))
            .AddPlayerWorld(
                itemPoolFactory: sp =>
                {
                    var refData = sp.GetRequiredService<IReferenceDataService>();
                    var keys = refData.ItemsByInternalName.Keys;
                    var identity = keys.ToFrozenDictionary(k => k, k => k, StringComparer.Ordinal);
                    return new Arda.Dispatch.InternPool(identity);
                },
                projectToGameHourFactory: _ => at => GameClock.Project(at).Hour,
                shiftsFactory: sp =>
                {
                    var catalog = sp.GetRequiredService<IShiftCatalog>();
                    return catalog.Shifts.Select(s => (s.Slug, s.StartHour)).ToList();
                })
            .AddChatWorld();

        services.AddSingleton<InventoryProjection>();

        services.AddSingleton<WorldHealthView>();
        services.AddSingleton<IWorldHealthView>(sp => sp.GetRequiredService<WorldHealthView>());
        services.AddSingleton<IAttentionSource>(sp => sp.GetRequiredService<WorldHealthView>());
        services.AddHostedService(sp => sp.GetRequiredService<WorldHealthView>());

        services.AddHostedService<ActiveCharacterLogSynchronizer>(sp => new ActiveCharacterLogSynchronizer(
            sp.GetRequiredService<Arda.Contracts.IDomainEventSubscriber>(),
            sp.GetRequiredService<IActiveCharacterService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("ActiveChar")));

        services.AddSingleton<SessionAgreementComposer>(sp => new SessionAgreementComposer(
            sp.GetRequiredService<Arda.Contracts.IDomainEventSubscriber>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("SessionAgreement")));
        services.AddSingleton<IAttentionSource>(sp => sp.GetRequiredService<SessionAgreementComposer>());
        services.AddHostedService(sp => sp.GetRequiredService<SessionAgreementComposer>());

        return services;
    }
}
