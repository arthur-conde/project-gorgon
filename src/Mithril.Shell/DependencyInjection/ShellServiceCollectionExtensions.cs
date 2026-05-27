using System.IO;
using System.Reflection;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Diagnostics.Performance;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Modules;
using Mithril.Shared.Wpf;
using Mithril.Shell.Hotkeys;
using Mithril.Shell.Updates;
using Mithril.Shell.ViewModels;
using Mithril.Shell.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mithril.Shell.DependencyInjection;

public sealed class DiscoveredModules(IReadOnlyList<IMithrilModule> modules)
{
    public IReadOnlyList<IMithrilModule> Modules => modules;
}

public static class ShellServiceCollectionExtensions
{
    /// <summary>
    /// Informational version (Nerdbank.GitVersioning <c>X.Y.Z.N+sha</c>) of an
    /// assembly, falling back to the plain assembly version. Used for the
    /// shell/module version dump in boot.log so a stale or mismatched module
    /// is visible at a glance.
    /// </summary>
    internal static string InformationalVersion(this Assembly asm) =>
        asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? asm.GetName().Version?.ToString()
        ?? "unknown";

    public static IServiceCollection AddMithrilModules(
        this IServiceCollection services, Action<string>? log = null)
    {
        using var activity = Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources.ShellModules.StartActivity("discover");
        var modulesDir = Path.Combine(AppContext.BaseDirectory, "modules");
        var modules = new List<IMithrilModule>();

        if (Directory.Exists(modulesDir))
        {
            foreach (var dll in Directory.EnumerateFiles(modulesDir, "*.dll"))
            {
                Assembly asm;
                try { asm = Assembly.LoadFrom(dll); }
                catch { continue; }

                foreach (var t in SafeGetTypes(asm))
                {
                    if (t is { IsClass: true, IsAbstract: false } &&
                        typeof(IMithrilModule).IsAssignableFrom(t))
                    {
                        if (Activator.CreateInstance(t) is IMithrilModule m)
                            modules.Add(m);
                    }
                }
            }
        }

        foreach (var module in modules)
        {
            module.Register(services);
            services.AddSingleton<IMithrilModule>(module);
            var asm = module.GetType().Assembly;
            log?.Invoke($"module loaded: {module.Id} [{asm.GetName().Name}] {asm.InformationalVersion()}");
        }

        services.AddSingleton(new DiscoveredModules(modules));
        activity?.SetTag("discovered_count", (long)modules.Count);
        return services;
    }

    public static IServiceCollection AddMithrilAttention(this IServiceCollection services) =>
        services.AddSingleton<IAttentionAggregator>(sp => new AttentionAggregator(
            sp.GetServices<IAttentionSource>(),
            dispatch: a =>
            {
                var d = System.Windows.Application.Current?.Dispatcher;
                if (d is null || d.CheckAccess()) a();
                else d.InvokeAsync(a);
            }));

    public static IServiceCollection AddMithrilShellUpdates(this IServiceCollection services) =>
        services
            .AddSingleton<UpdateChannelInfo>(_ => UpdateChannelInfo.FromEmbedded())
            .AddSingleton<MithrilUpdateManager>()
            .AddSingleton<IUpdateStatusService, UpdateStatusService>()
            .AddSingleton<IUpdateChecker>(sp => new VelopackUpdateChecker(
                sp.GetRequiredService<MithrilUpdateManager>(),
                sp.GetRequiredService<IUpdateStatusService>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("updates")))
            .AddSingleton<IUpdateApplier>(sp => new VelopackUpdateApplier(
                sp.GetRequiredService<MithrilUpdateManager>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("updates")))
            .AddHostedService<UpdateCheckHostedService>();

    public static IServiceCollection AddMithrilItemDetail(this IServiceCollection services) =>
        services
            .AddSingleton<IItemDetailPresenter, ItemDetailPresenter>()
            .AddSingleton<IModuleActivator, ShellModuleActivator>()
            // Shell-side deep-link handlers: depend only on IReferenceNavigator
            // (NoOp when Silmarillion isn't loaded), so they belong here, not in
            // a module's Register(). Modules register their own action handlers.
            .AddSingleton<IDeepLinkHandler, ItemDeepLinkHandler>()
            .AddSingleton<IDeepLinkHandler, RecipeDeepLinkHandler>()
            // Router pulls every registered IDeepLinkHandler. Module-side handlers
            // register from their own IMithrilModule.Register() implementations.
            .AddSingleton<IDeepLinkRouter>(sp => new DeepLinkRouter(
                sp.GetServices<IDeepLinkHandler>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("DeepLink")));

    public static IServiceCollection AddMithrilIngredientSources(this IServiceCollection services) =>
        services.AddSingleton<IIngredientSourcesPresenter, IngredientSourcesPresenter>();

    public static IServiceCollection AddMithrilShellCommands(this IServiceCollection services) =>
        services
            .AddSingleton<IHotkeyCommand>(sp => new ForceQuitCommand(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Shell")))
            .AddSingleton<IHotkeyCommand>(sp => new StartPerfTraceHotkey(
                sp.GetRequiredService<ShellSettings>(),
                sp.GetRequiredService<PerfRecorderHostedService>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("PerfTrace")));

    public static IServiceCollection AddMithrilShellViews(this IServiceCollection services) =>
        services
            // ViewModels
            .AddSingleton<ShellViewModel>()
            .AddSingleton<GameConfigViewModel>()
            .AddSingleton<IconSettingsViewModel>()
            .AddSingleton<HotkeyBindingsViewModel>()
            .AddSingleton<DiagnosticsViewModel>()
            .AddSingleton<ReferenceDataViewModel>()
            .AddSingleton<AppearanceSettingsViewModel>()
            .AddSingleton<AboutSettingsViewModel>()
            .AddSingleton<DiagnosticsSettingsViewModel>()
            .AddSingleton<SettingsHostViewModel>()
            // Views
            .AddSingleton<ShellWindow>()
            .AddSingleton<GameConfigView>(sp => new GameConfigView
            {
                DataContext = sp.GetRequiredService<GameConfigViewModel>(),
            })
            .AddSingleton<IconSettingsView>(sp => new IconSettingsView
            {
                DataContext = sp.GetRequiredService<IconSettingsViewModel>(),
            })
            .AddSingleton<HotkeyBindingsView>(sp => new HotkeyBindingsView
            {
                DataContext = sp.GetRequiredService<HotkeyBindingsViewModel>(),
            })
            .AddSingleton<DiagnosticsView>(sp => new DiagnosticsView
            {
                DataContext = sp.GetRequiredService<DiagnosticsViewModel>(),
            })
            .AddSingleton<ReferenceDataView>(sp => new ReferenceDataView
            {
                DataContext = sp.GetRequiredService<ReferenceDataViewModel>(),
            })
            .AddSingleton<AppearanceSettingsView>(sp => new AppearanceSettingsView
            {
                DataContext = sp.GetRequiredService<AppearanceSettingsViewModel>(),
            })
            .AddSingleton<AboutSettingsView>(sp => new AboutSettingsView
            {
                DataContext = sp.GetRequiredService<AboutSettingsViewModel>(),
            })
            .AddSingleton<DiagnosticsSettingsView>(sp => new DiagnosticsSettingsView
            {
                DataContext = sp.GetRequiredService<DiagnosticsSettingsViewModel>(),
            })
            .AddSingleton<SettingsHostView>(sp => new SettingsHostView
            {
                DataContext = sp.GetRequiredService<SettingsHostViewModel>(),
            });

    private static Type[] SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null).ToArray()!; }
    }
}
