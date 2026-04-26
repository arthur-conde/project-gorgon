using System.IO;
using System.Reflection;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;
using Mithril.Shared.Wpf;
using Mithril.Shell.Updates;
using Mithril.Shell.ViewModels;
using Mithril.Shell.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shell.DependencyInjection;

public sealed class DiscoveredModules(IReadOnlyList<IMithrilModule> modules)
{
    public IReadOnlyList<IMithrilModule> Modules => modules;
}

public static class ShellServiceCollectionExtensions
{
    public static IServiceCollection AddMithrilModules(this IServiceCollection services)
    {
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
        }

        services.AddSingleton(new DiscoveredModules(modules));
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
            .AddSingleton<IUpdateChecker, VelopackUpdateChecker>()
            .AddSingleton<IUpdateApplier, VelopackUpdateApplier>()
            .AddHostedService<UpdateCheckHostedService>();

    public static IServiceCollection AddMithrilItemDetail(this IServiceCollection services) =>
        services
            .AddSingleton<IItemDetailPresenter, ItemDetailPresenter>()
            .AddSingleton<IModuleActivator, ShellModuleActivator>()
            // Factory form so ICraftListImportTarget and IDiagnosticsSink stay optional —
            // modules register them at their discretion, and the router degrades gracefully
            // (deep links to absent handlers are logged + dropped, never thrown).
            .AddSingleton<IDeepLinkRouter>(sp => new DeepLinkRouter(
                sp.GetRequiredService<IItemDetailPresenter>(),
                sp.GetService<ICraftListImportTarget>(),
                sp.GetService<IDiagnosticsSink>()));

    public static IServiceCollection AddMithrilIngredientSources(this IServiceCollection services) =>
        services.AddSingleton<IIngredientSourcesPresenter, IngredientSourcesPresenter>();

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
