using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using Gorgon.Shared.Game;
using Gorgon.Shared.Hotkeys;
using Gorgon.Shared.Logging;
using Gorgon.Shared.Modules;
using Gorgon.Shared.Settings;
using Gorgon.Shell.ViewModels;
using Gorgon.Shell.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Gorgon.Shell;

public partial class App : Application
{
    private const string MutexName = @"Global\Gorgon.Shell.SingleInstance";
    private IHost? _host;
    private Mutex? _mutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool createdNew;
        _mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        if (!createdNew)
        {
            // Another instance is running — exit silently. (TODO: signal it to foreground.)
            Shutdown();
            return;
        }

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var shellDir = Path.Combine(localApp, "Gorgon", "Shell");
        Directory.CreateDirectory(shellDir);
        var shellSettingsPath = Path.Combine(shellDir, "shell.json");

        var builder = Host.CreateApplicationBuilder(e.Args);

        // Shell settings
        var shellStore = new JsonSettingsStore<ShellSettings>(shellSettingsPath, ShellSettingsJsonContext.Default.ShellSettings);
        var shellSettings = await shellStore.LoadAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(shellSettings.GameRoot))
            shellSettings.GameRoot = GameLocator.AutoDetectGameRoot() ?? "";
        builder.Services.AddSingleton<ISettingsStore<ShellSettings>>(shellStore);
        builder.Services.AddSingleton(shellSettings);

        // Game config (live, observable)
        var gameConfig = new GameConfig { GameRoot = shellSettings.GameRoot };
        gameConfig.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(GameConfig.GameRoot))
                shellSettings.GameRoot = gameConfig.GameRoot;
        };
        builder.Services.AddSingleton(gameConfig);

        // Shared services
        builder.Services.AddSingleton<IPlayerLogStream, PlayerLogStream>();
        builder.Services.AddSingleton<HotkeyRegistry>();
        builder.Services.AddSingleton<IHotkeyService, HotkeyService>();

        // Module discovery: scan all loaded assemblies for IGorgonModule impls
        var modules = DiscoverModules();
        foreach (var module in modules)
        {
            module.Register(builder.Services);
            builder.Services.AddSingleton<IGorgonModule>(module);
        }

        // Shell VMs
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<GameConfigViewModel>();
        builder.Services.AddSingleton<HotkeyBindingsViewModel>();
        builder.Services.AddSingleton<ShellWindow>();
        builder.Services.AddSingleton<GameConfigView>();
        builder.Services.AddSingleton<HotkeyBindingsView>();

        _host = builder.Build();
        await _host.StartAsync().ConfigureAwait(true);

        // Construct shell window, attach hotkey hwnd
        var shell = _host.Services.GetRequiredService<ShellWindow>();
        shell.DataContext = _host.Services.GetRequiredService<ShellViewModel>();
        shell.Left = shellSettings.WindowLeft;
        shell.Top = shellSettings.WindowTop;
        shell.Width = shellSettings.WindowWidth;
        shell.Height = shellSettings.WindowHeight;
        MainWindow = shell;

        // Wire view DataContexts
        _host.Services.GetRequiredService<GameConfigView>().DataContext = _host.Services.GetRequiredService<GameConfigViewModel>();
        _host.Services.GetRequiredService<HotkeyBindingsView>().DataContext = _host.Services.GetRequiredService<HotkeyBindingsViewModel>();

        shell.Show();
        var hwnd = new WindowInteropHelper(shell).Handle;
        var hk = _host.Services.GetRequiredService<IHotkeyService>();
        hk.Attach(hwnd);
        hk.ReloadFromBindings(shellSettings.HotkeyBindings.Values);
    }

    private static List<IGorgonModule> DiscoverModules()
    {
        // Force-load Samwise; future modules added via ProjectReference are scanned next.
        var loaded = new HashSet<string>();
        var queue = new Queue<Assembly>();
        queue.Enqueue(typeof(Samwise.SamwiseModule).Assembly);
        var result = new List<IGorgonModule>();
        while (queue.Count > 0)
        {
            var asm = queue.Dequeue();
            if (!loaded.Add(asm.FullName ?? asm.GetName().Name ?? "")) continue;
            foreach (var t in SafeGetTypes(asm))
            {
                if (t is { IsClass: true, IsAbstract: false } && typeof(IGorgonModule).IsAssignableFrom(t))
                {
                    if (Activator.CreateInstance(t) is IGorgonModule m) result.Add(m);
                }
            }
        }
        return result;
    }

    private static Type[] SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null).ToArray()!; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                var settings = _host.Services.GetRequiredService<ShellSettings>();
                if (MainWindow is { } w && w.WindowState == WindowState.Normal)
                {
                    settings.WindowLeft = w.Left; settings.WindowTop = w.Top;
                    settings.WindowWidth = w.Width; settings.WindowHeight = w.Height;
                }
                _host.Services.GetRequiredService<ISettingsStore<ShellSettings>>().Save(settings);
            }
            catch { }
            try { _host.StopAsync(TimeSpan.FromSeconds(2)).Wait(TimeSpan.FromSeconds(3)); } catch { }
            try { _host.Dispose(); } catch { }
        }
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
