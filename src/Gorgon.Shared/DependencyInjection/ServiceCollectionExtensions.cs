using System.Net.Http;
using Gorgon.Shared.Character;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Hotkeys;
using Gorgon.Shared.Icons;
using Gorgon.Shared.Logging;
using Gorgon.Shared.Modules;
using Gorgon.Shared.Reference;
using Gorgon.Shared.Settings;
using Gorgon.Shared.Wpf.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace Gorgon.Shared.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGorgonDiagnostics(this IServiceCollection services, string logDirectory) =>
        services.AddSingleton<IDiagnosticsSink>(_ =>
            new SerilogDiagnosticsSink(new DiagnosticsSink(), logDirectory));

    public static IServiceCollection AddGorgonGameServices(this IServiceCollection services) =>
        services
            .AddSingleton<IPlayerLogStream, PlayerLogStream>()
            .AddSingleton<IChatLogStream, ChatLogStream>()
            .AddSingleton<IActiveCharacterService>(sp => new ActiveCharacterService(
                sp.GetRequiredService<Game.GameConfig>(),
                sp.GetRequiredService<IActiveCharacterPersistence>(),
                sp.GetRequiredService<IDiagnosticsSink>()))
            .AddHostedService<ActiveCharacterLogSynchronizer>();

    public static IServiceCollection AddGorgonReferenceData(this IServiceCollection services, string cacheDirectory) =>
        services
            .AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            .AddSingleton<IReferenceDataService>(sp => new ReferenceDataService(
                cacheDirectory,
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IDiagnosticsSink>()));

    public static IServiceCollection AddGorgonCommunityCalibration(this IServiceCollection services, string cacheDirectory) =>
        services
            .AddSingleton<ICommunityCalibrationService>(sp => new CommunityCalibrationService(
                cacheDirectory,
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IDiagnosticsSink>()));

    public static IServiceCollection AddGorgonIcons(this IServiceCollection services, string cacheDirectory)
    {
        var settingsPath = System.IO.Path.Combine(cacheDirectory, "settings.json");
        return services
            .AddSingleton<ISettingsStore<IconSettings>>(_ =>
                new JsonSettingsStore<IconSettings>(settingsPath, IconSettingsJsonContext.Default.IconSettings))
            .AddSingleton(sp =>
                sp.GetRequiredService<ISettingsStore<IconSettings>>().Load())
            .AddSingleton<SettingsAutoSaver<IconSettings>>()
            .AddSingleton<IIconCacheService>(sp => new IconCacheService(
                cacheDirectory,
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IReferenceDataService>(),
                sp.GetRequiredService<IDiagnosticsSink>(),
                sp.GetRequiredService<IconSettings>()));
    }

    public static IServiceCollection AddGorgonHotkeys(this IServiceCollection services) =>
        services
            .AddSingleton<HotkeyRegistry>()
            .AddSingleton<IHotkeyService, HotkeyService>();

    public static IServiceCollection AddGorgonModuleGates(this IServiceCollection services) =>
        services.AddSingleton<ModuleGates>();

    public static IServiceCollection AddGorgonDialogs(this IServiceCollection services) =>
        services.AddSingleton<IDialogService, DialogService>();
}
