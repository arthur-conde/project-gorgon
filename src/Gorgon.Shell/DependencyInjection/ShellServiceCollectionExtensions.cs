using System.IO;
using System.Reflection;
using Gorgon.Shared.Modules;
using Gorgon.Shell.Updates;
using Gorgon.Shell.ViewModels;
using Gorgon.Shell.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Gorgon.Shell.DependencyInjection;

public sealed class DiscoveredModules(IReadOnlyList<IGorgonModule> modules)
{
    public IReadOnlyList<IGorgonModule> Modules => modules;
}

public static class ShellServiceCollectionExtensions
{
    public static IServiceCollection AddGorgonModules(this IServiceCollection services)
    {
        var modulesDir = Path.Combine(AppContext.BaseDirectory, "modules");
        var modules = new List<IGorgonModule>();

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
                        typeof(IGorgonModule).IsAssignableFrom(t))
                    {
                        if (Activator.CreateInstance(t) is IGorgonModule m)
                            modules.Add(m);
                    }
                }
            }
        }

        foreach (var module in modules)
        {
            module.Register(services);
            services.AddSingleton<IGorgonModule>(module);
        }

        services.AddSingleton(new DiscoveredModules(modules));
        return services;
    }

    public static IServiceCollection AddGorgonShellUpdates(this IServiceCollection services) =>
        services
            .AddSingleton<IUpdateStatusService, UpdateStatusService>()
            .AddSingleton<IUpdateChecker, GitHubUpdateChecker>()
            .AddHostedService<UpdateCheckHostedService>();

    public static IServiceCollection AddGorgonShellViews(this IServiceCollection services) =>
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
