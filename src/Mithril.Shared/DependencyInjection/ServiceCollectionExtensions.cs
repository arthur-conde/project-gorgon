using System.ComponentModel;
using System.Net.Http;
using System.Text.Json.Serialization.Metadata;
using Mithril.GameReports;
using Mithril.Shared.Audio;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Diagnostics.Performance;
using Mithril.Shared.Game;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Icons;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shared.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMithrilDiagnostics(this IServiceCollection services, string logDirectory) =>
        services.AddSingleton<IDiagnosticsSink>(_ =>
            new SerilogDiagnosticsSink(new DiagnosticsSink(), logDirectory));

    /// <summary>
    /// Register the opt-in perf-trace harness: an <see cref="IPerfTracer"/>
    /// singleton (writes per-session JSON-lines files to <paramref name="perfDirectory"/>)
    /// plus a <see cref="PerfTracerHostedService"/> that owns the WPF hooks
    /// (CompositionTarget, Dispatcher.Hooks, InputManager, GC polling, counters,
    /// binding errors) and toggles them on/off in sync with session state.
    ///
    /// <paramref name="verboseFrameEventsAccessor"/> is a late-bound read of the
    /// shell setting so flipping <c>ShellSettings.VerboseFrameEvents</c> takes
    /// effect mid-session without a restart.
    /// </summary>
    public static IServiceCollection AddMithrilPerfTrace(
        this IServiceCollection services,
        string perfDirectory,
        Func<IServiceProvider, Func<bool>> verboseFrameEventsAccessor)
    {
        services.AddSingleton<IPerfTracer>(sp => new PerfTracer(
            perfDirectory,
            sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton<PerfTracerHostedService>(sp => new PerfTracerHostedService(
            sp.GetRequiredService<IPerfTracer>(),
            sp.GetRequiredService<IActiveCharacterService>(),
            sp.GetServices<Mithril.Shared.Modules.IMithrilModule>(),
            verboseFrameEventsAccessor(sp),
            sp.GetService<IDiagnosticsSink>()));
        services.AddHostedService(sp => sp.GetRequiredService<PerfTracerHostedService>());
        return services;
    }

    public static IServiceCollection AddMithrilGameServices(this IServiceCollection services) =>
        services
            .AddSingleton<IGameClock, GameClock>()
            // Shift catalog is bundled JSON with a hardcoded fallback —
            // critical-path for the shell's "next shift" countdown and the
            // Gandalf shift-alarm scheduler, so we'd rather degrade to stale
            // data than to no data on a bundled-file failure.
            .AddSingleton<IShiftCatalog>(sp => new JsonShiftCatalog(
                bundledDir: null,
                diag: sp.GetService<IDiagnosticsSink>()))
            // SessionAnchor is a leaf so PlayerLogStream / ChatLogStream can
            // resolve ISessionAnchor without forming a cycle with the higher-
            // level GameSessionService (which CONSUMES the stream and PUSHES
            // to the anchor — see SessionAnchor.cs).
            .AddSingleton<SessionAnchor>()
            .AddSingleton<ISessionAnchor>(sp => sp.GetRequiredService<SessionAnchor>())
            .AddSingleton<IPlayerLogStream, PlayerLogStream>()
            .AddSingleton<IChatLogStream, ChatLogStream>()
            // Foundation service (#612) — owns the FileSystemWatcher on
            // Reports/, parses storage exports + character snapshots,
            // exposes per-(server, character) scope query. ActiveCharacterService
            // is now a thin adapter that adds the "active selection" axis.
            .AddSingleton<IGameReportsService>(sp =>
            {
                var gameConfig = sp.GetRequiredService<Game.GameConfig>();
                var diag = sp.GetService<IDiagnosticsSink>();
                return new GameReportsService(
                    () => gameConfig.ReportsDirectory,
                    diag is null ? null : (category, message) => diag.Write(DiagnosticLevel.Warn, category, message));
            })
            .AddSingleton<IActiveCharacterService>(sp => new ActiveCharacterService(
                sp.GetRequiredService<Game.GameConfig>(),
                sp.GetRequiredService<IActiveCharacterPersistence>(),
                sp.GetRequiredService<IGameReportsService>(),
                sp.GetRequiredService<IDiagnosticsSink>()))
            .AddHostedService<ActiveCharacterLogSynchronizer>();

    /// <summary>
    /// Register the L0.5 classifier + splitter pair (#556). The
    /// <see cref="PlayerLogClassifier"/> consumes <see cref="IPlayerLogStream"/>,
    /// classifies each line, and publishes the surviving ~5% on the
    /// unified <see cref="IClassifiedPlayerLogStream"/> pipe. The
    /// <see cref="PlayerLogPipeSplitter"/> subscribes to that unified pipe
    /// and fans out to three per-Kind typed pipes:
    /// <see cref="ILocalPlayerLogStream"/>, <see cref="ICombatActorLogStream"/>,
    /// <see cref="ISystemSignalLogStream"/>.
    ///
    /// <para>Cross-pipe-ordering-sensitive consumers subscribe to the
    /// unified pipe via the L1 driver (using
    /// <see cref="IClassifiedPlayerLogLine"/> as the subscription type).
    /// Consumers needing only one Kind subscribe to the typed pipes.</para>
    ///
    /// <para><paramref name="captureRawAccessor"/> is a late-bound read of an
    /// infra diagnostic setting (typically a <c>ShellSettings</c> property)
    /// — when it returns <c>true</c> the classifier fills the <c>Raw</c>
    /// field on emitted records with the exact source line; when
    /// <c>false</c> (the default) <c>Raw</c> stays <c>null</c> and no
    /// per-line string allocation occurs. Mirrors the perf-trace pattern:
    /// flip the setting and subsequent emissions reflect the new state.</para>
    /// </summary>
    public static IServiceCollection AddMithrilLogActorPipeline(
        this IServiceCollection services,
        Func<IServiceProvider, Func<bool>>? captureRawAccessor = null)
    {
        services.AddSingleton<PlayerLogClassifier>(sp => new PlayerLogClassifier(
            sp.GetRequiredService<IPlayerLogStream>(),
            sp.GetService<IDiagnosticsSink>(),
            captureRawAccessor?.Invoke(sp)));
        services.AddSingleton<IClassifiedPlayerLogStream>(sp =>
            sp.GetRequiredService<PlayerLogClassifier>());

        services.AddSingleton<PlayerLogPipeSplitter>(sp => new PlayerLogPipeSplitter(
            sp.GetRequiredService<IClassifiedPlayerLogStream>(),
            sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton<ILocalPlayerLogStream>(sp =>
            sp.GetRequiredService<PlayerLogPipeSplitter>());
        services.AddSingleton<ICombatActorLogStream>(sp =>
            sp.GetRequiredService<PlayerLogPipeSplitter>());
        services.AddSingleton<ISystemSignalLogStream>(sp =>
            sp.GetRequiredService<PlayerLogPipeSplitter>());
        return services;
    }

    /// <summary>
    /// Register the L1 subscription driver (#511 deliverable 3 / #550 PR 1).
    /// Sits between the L0.5 typed pipes + unified pipe (registered by
    /// <see cref="AddMithrilLogActorPipeline"/>) + the L0
    /// <see cref="IChatLogStream"/>, and produces typed
    /// <see cref="LogEnvelope{T}"/> subscriptions with cross-cutting
    /// concerns owned by the driver: <see cref="ReplayMode"/>,
    /// <see cref="LogEnvelope{T}.IsReplay"/>, per-handler containment,
    /// drop accounting, <see cref="DeliveryContext"/> marshalling,
    /// opt-in <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/>
    /// idempotence filter, and a per-subscription fault SM that surfaces
    /// degraded subscriptions on <see cref="IAttentionAggregator"/> via the
    /// <see cref="LogStreamAttentionSource"/> registered alongside.
    ///
    /// <para>The producer + consumer fleets now route through this driver
    /// (archetype-A migrations landed across #555 / #560–#564; archetype-A
    /// shared GameState — Pin/Weather/Position — migrated to the unified
    /// pipe in #556 Phase 3 / #569). Archetype-B consumers continue to
    /// migrate per the #550 plan; un-migrated consumers still subscribe to
    /// the L0 / L0.5 surfaces directly until their PR lands.</para>
    /// </summary>
    public static IServiceCollection AddMithrilLogStreamDriver(this IServiceCollection services)
    {
        services.AddSingleton<LogStreamAttentionSource>();
        // Surface the attention source via the shared IAttentionSource
        // contract so AttentionAggregator picks it up alongside the
        // per-module sources.
        services.AddSingleton<Modules.IAttentionSource>(sp => sp.GetRequiredService<LogStreamAttentionSource>());
        services.AddSingleton<ILogStreamDriver>(sp => new LogStreamDriver(
            sp.GetRequiredService<ILocalPlayerLogStream>(),
            sp.GetRequiredService<ICombatActorLogStream>(),
            sp.GetRequiredService<ISystemSignalLogStream>(),
            sp.GetRequiredService<IClassifiedPlayerLogStream>(),
            sp.GetRequiredService<IChatLogStream>(),
            sp.GetRequiredService<LogStreamAttentionSource>(),
            sp.GetService<IDiagnosticsSink>()));
        return services;
    }

    /// <summary>
    /// Register the root directory for per-character storage (typically
    /// <c>%LocalAppData%/Mithril/characters</c>). Must be called before any
    /// <see cref="AddPerCharacterStore{T}"/> / <see cref="AddPerCharacterModuleStore{T}"/>.
    /// </summary>
    public static IServiceCollection AddMithrilPerCharacterStorage(
        this IServiceCollection services,
        string charactersRootDir)
    {
        services.AddSingleton(new PerCharacterStoreOptions { CharactersRootDir = charactersRootDir });
        services.AddPerCharacterStore<CharacterPresence>("character.json", CharacterPresenceJsonContext.Default.CharacterPresence);
        services.AddSingleton<CharacterPresenceService>();
        services.AddSingleton<ICharacterPresenceService>(sp => sp.GetRequiredService<CharacterPresenceService>());
        services.AddHostedService(sp => sp.GetRequiredService<CharacterPresenceService>());
        return services;
    }

    /// <summary>
    /// Register a <see cref="PerCharacterStore{T}"/> + <see cref="PerCharacterView{T}"/> pair.
    /// If an <see cref="ILegacyMigration{T}"/> has already been registered, the store will
    /// pick it up automatically.
    /// </summary>
    public static IServiceCollection AddPerCharacterStore<T>(
        this IServiceCollection services,
        string fileName,
        JsonTypeInfo<T> typeInfo)
        where T : class, IVersionedState<T>, new()
    {
        services.AddSingleton(sp => new PerCharacterStore<T>(
            sp.GetRequiredService<PerCharacterStoreOptions>().CharactersRootDir,
            fileName,
            typeInfo,
            sp.GetService<ILegacyMigration<T>>(),
            sp.GetService<IDiagnosticsSink>()));
        services.AddSingleton(sp => new PerCharacterView<T>(
            sp.GetRequiredService<IActiveCharacterService>(),
            sp.GetRequiredService<PerCharacterStore<T>>()));
        return services;
    }

    /// <summary>
    /// Per-character/per-module convenience: file name is derived from the module id
    /// (<c>"{moduleId}.json"</c>).
    /// </summary>
    public static IServiceCollection AddPerCharacterModuleStore<T>(
        this IServiceCollection services,
        string moduleId,
        JsonTypeInfo<T> typeInfo)
        where T : class, IVersionedState<T>, new()
    {
        if (string.IsNullOrEmpty(moduleId)) throw new ArgumentException("moduleId required", nameof(moduleId));
        return services.AddPerCharacterStore<T>($"{moduleId}.json", typeInfo);
    }

    public static IServiceCollection AddMithrilReferenceData(this IServiceCollection services, string cacheDirectory) =>
        services
            .AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            .AddSingleton<IReferenceDataService>(sp => new ReferenceDataService(
                cacheDirectory,
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IDiagnosticsSink>(),
                perf: sp.GetService<IPerfTracer>()))
            .AddSingleton<IEntityNameResolver, ReferenceDataEntityNameResolver>();

    public static IServiceCollection AddMithrilCommunityCalibration(this IServiceCollection services, string cacheDirectory) =>
        services
            .AddSingleton<ICommunityCalibrationService>(sp => new CommunityCalibrationService(
                cacheDirectory,
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IDiagnosticsSink>()));

    /// <summary>
    /// Register a settings type for JSON persistence with debounced autosave on
    /// every <see cref="INotifyPropertyChanged.PropertyChanged"/> event. Registers:
    /// <list type="bullet">
    /// <item><see cref="ISettingsStore{T}"/> backed by <see cref="JsonSettingsStore{T}"/> at <paramref name="settingsPath"/></item>
    /// <item><typeparamref name="T"/> as a singleton, loaded from the store</item>
    /// <item><see cref="SettingsAutoSaver{T}"/> as both singleton and <c>IHostedService</c>, so Generic Host always activates it eagerly at startup</item>
    /// </list>
    /// The hosted-service registration is what closes the silent-failure foot-gun
    /// from #101: previously each module had to remember to inject the saver
    /// somewhere or use a discard-resolve in a factory; now activation is automatic.
    /// </summary>
    public static IServiceCollection AddMithrilSettings<T>(
        this IServiceCollection services,
        string settingsPath,
        JsonTypeInfo<T> typeInfo)
        where T : class, INotifyPropertyChanged, new()
    {
        services.AddSingleton<ISettingsStore<T>>(_ =>
            new JsonSettingsStore<T>(settingsPath, typeInfo));
        services.AddSingleton<T>(sp =>
            sp.GetRequiredService<ISettingsStore<T>>().Load());
        services.AddSingleton<SettingsAutoSaver<T>>();
        services.AddHostedService(sp => sp.GetRequiredService<SettingsAutoSaver<T>>());
        return services;
    }

    /// <summary>
    /// Variant of <see cref="AddMithrilSettings{T}"/> for settings types that
    /// implement <see cref="IVersionedState{T}"/>. After loading, dispatches
    /// through <c>T.Migrate</c> when the persisted <c>SchemaVersion</c> doesn't
    /// match <c>T.CurrentVersion</c>, then writes the migrated form back so the
    /// legacy fields don't linger on disk. Mirrors <see cref="PerCharacterStore{T}"/>'s
    /// dispatch — kept as a separate overload so the constraint failure surfaces
    /// at the call site instead of at runtime.
    /// </summary>
    public static IServiceCollection AddMithrilVersionedSettings<T>(
        this IServiceCollection services,
        string settingsPath,
        JsonTypeInfo<T> typeInfo)
        where T : class, INotifyPropertyChanged, IVersionedState<T>, new()
    {
        services.AddSingleton<ISettingsStore<T>>(_ =>
            new JsonSettingsStore<T>(settingsPath, typeInfo));
        services.AddSingleton<T>(sp =>
        {
            var store = sp.GetRequiredService<ISettingsStore<T>>();
            var loaded = store.Load();
            if (loaded.SchemaVersion != T.CurrentVersion)
            {
                loaded = T.Migrate(loaded);
                loaded.SchemaVersion = T.CurrentVersion;
                store.Save(loaded);
            }
            return loaded;
        });
        services.AddSingleton<SettingsAutoSaver<T>>();
        services.AddHostedService(sp => sp.GetRequiredService<SettingsAutoSaver<T>>());
        return services;
    }

    public static IServiceCollection AddMithrilIcons(this IServiceCollection services, string cacheDirectory)
    {
        var settingsPath = System.IO.Path.Combine(cacheDirectory, "settings.json");
        services.AddMithrilSettings<IconSettings>(settingsPath, IconSettingsJsonContext.Default.IconSettings);
        services.AddSingleton<IIconCacheService>(sp => new IconCacheService(
            cacheDirectory,
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IReferenceDataService>(),
            sp.GetRequiredService<IDiagnosticsSink>(),
            sp.GetRequiredService<IconSettings>()));
        return services;
    }

    public static IServiceCollection AddMithrilAudio(this IServiceCollection services) =>
        services.AddSingleton<IAudioPlaybackSink, StaticAudioPlayerSink>();

    public static IServiceCollection AddMithrilHotkeys(this IServiceCollection services)
    {
        services.TryAddSingleton<IHotkeyGate, AlwaysOpenHotkeyGate>();
        return services
            .AddSingleton<HotkeyRegistry>()
            .AddSingleton<IHotkeyService, HotkeyService>();
    }

    public static IServiceCollection AddMithrilModuleGates(this IServiceCollection services) =>
        services.AddSingleton<ModuleGates>();

}
