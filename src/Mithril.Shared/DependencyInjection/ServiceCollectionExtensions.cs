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
using Microsoft.Extensions.Logging;

namespace Mithril.Shared.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the opt-in perf-recording harness: an <see cref="IPerfRecorder"/>
    /// singleton (writes per-session JSON-lines files to <paramref name="perfDirectory"/>
    /// while a session is active) plus a <see cref="PerfRecorderHostedService"/>
    /// that owns the WPF hooks (CompositionTarget, Dispatcher.Hooks, InputManager,
    /// GC polling, counters, binding errors) and toggles them on/off in sync with
    /// session state.
    ///
    /// Producers emit via <see cref="Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources"/>
    /// + <see cref="Mithril.Shared.Diagnostics.Telemetry.MithrilMeters"/> — the
    /// recorder's internal listener serialises them into the JSON-lines schema
    /// documented in <c>docs/perf-trace-schema.md</c>.
    ///
    /// <paramref name="verboseFrameEventsAccessor"/> is a late-bound read of the
    /// shell setting so flipping <c>ShellSettings.VerboseFrameEvents</c> takes
    /// effect mid-session without a restart.
    /// </summary>
    public static IServiceCollection AddMithrilPerfRecorder(
        this IServiceCollection services,
        string perfDirectory,
        Func<IServiceProvider, Func<bool>> verboseFrameEventsAccessor)
    {
        services.AddSingleton<IPerfRecorder>(sp => new PerfRecorder(
            perfDirectory,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("PerfTrace")));
        services.AddSingleton<PerfRecorderHostedService>(sp => new PerfRecorderHostedService(
            sp.GetRequiredService<IPerfRecorder>(),
            sp.GetRequiredService<IActiveCharacterService>(),
            sp.GetServices<Mithril.Shared.Modules.IMithrilModule>(),
            verboseFrameEventsAccessor(sp),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("PerfTrace")));
        services.AddHostedService(sp => sp.GetRequiredService<PerfRecorderHostedService>());
        return services;
    }

    /// <summary>
    /// Register the core game-services graph (clocks, shift catalog,
    /// character snapshots). Log tailing is handled exclusively by the
    /// Arda pipeline (L0-L3).
    /// </summary>
    public static IServiceCollection AddMithrilGameServices(
        this IServiceCollection services) =>
        services
            .AddSingleton<IGameClock, GameClock>()
            .AddSingleton<IShiftCatalog>(sp => new JsonShiftCatalog(
                bundledDir: null,
                logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger("ShiftCatalog")))
            .AddSingleton<IGameReportsService>(sp =>
            {
                var gameConfig = sp.GetRequiredService<Game.GameConfig>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("GameReports");
                return new GameReportsService(
                    () => gameConfig.ReportsDirectory,
                    (category, message) => logger.LogWarning("{Category} {Detail}", category, message));
            })
            .AddSingleton<IActiveCharacterService>(sp => new ActiveCharacterService(
                sp.GetRequiredService<Game.GameConfig>(),
                sp.GetRequiredService<IActiveCharacterPersistence>(),
                sp.GetRequiredService<IGameReportsService>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("ActiveCharacter")));

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
            sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                ?.CreateLogger($"PerCharacterStore<{typeof(T).Name}>")));
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
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Reference")))
            .AddSingleton<IEntityNameResolver, ReferenceDataEntityNameResolver>();

    public static IServiceCollection AddMithrilCommunityCalibration(this IServiceCollection services, string cacheDirectory) =>
        services
            .AddSingleton<ICommunityCalibrationService>(sp => new CommunityCalibrationService(
                cacheDirectory,
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("CommunityCalibration")));

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
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Icons"),
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
