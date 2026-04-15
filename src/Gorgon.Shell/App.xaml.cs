using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using Gorgon.Shared.Diagnostics;
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

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Global\Gorgon.Shell.SingleInstance";
    private const string ActivateEventName = @"Global\Gorgon.Shell.Activate";
    private IHost? _host;
    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _activateCts;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            ShowFatal(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ShowFatal(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ShowFatal(args.Exception);
            args.SetObserved();
        };

        try { await StartCoreAsync(e); }
        catch (Exception ex) { ShowFatal(ex); Shutdown(); }
    }

    private static readonly string BootLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Gorgon", "Shell", "boot.log");

    private static void Boot(string step)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BootLogPath)!);
            File.AppendAllText(BootLogPath, $"{DateTime.Now:HH:mm:ss.fff} {step}\n");
        }
        catch { }
    }

    private async Task StartCoreAsync(StartupEventArgs e)
    {
        Boot("=== startup ===");
        bool createdNew;
        _mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        if (!createdNew)
        {
            // Signal the running instance to bring itself forward, then exit.
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ActivateEventName);
                ev.Set();
            }
            catch { }
            Shutdown();
            return;
        }
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateCts = new CancellationTokenSource();
        _ = WatchActivateEvent(_activateEvent, _activateCts.Token);

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

        // Shared services. Diagnostics sink is a ring-buffer decorated with
        // Serilog so every entry is also written to a JSON file for post-hoc analysis.
        var logDir = Path.Combine(shellDir, "logs");
        builder.Services.AddSingleton<IDiagnosticsSink>(_ =>
            new SerilogDiagnosticsSink(new DiagnosticsSink(), logDir));
        builder.Services.AddSingleton<IPlayerLogStream, PlayerLogStream>();
        builder.Services.AddSingleton<HotkeyRegistry>();
        builder.Services.AddSingleton<IHotkeyService, HotkeyService>();
        builder.Services.AddSingleton<ModuleGates>();

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
        builder.Services.AddSingleton<DiagnosticsViewModel>();
        builder.Services.AddSingleton<SettingsHostViewModel>();
        builder.Services.AddSingleton<ShellWindow>();
        builder.Services.AddSingleton<GameConfigView>(sp => new GameConfigView
        {
            DataContext = sp.GetRequiredService<GameConfigViewModel>(),
        });
        builder.Services.AddSingleton<HotkeyBindingsView>();
        builder.Services.AddSingleton<DiagnosticsView>(sp => new DiagnosticsView
        {
            DataContext = sp.GetRequiredService<DiagnosticsViewModel>(),
        });
        builder.Services.AddSingleton<SettingsHostView>(sp => new SettingsHostView
        {
            DataContext = sp.GetRequiredService<SettingsHostViewModel>(),
        });

        Boot($"modules discovered: {modules.Count}");
        _host = builder.Build();
        Boot("host built");
        await _host.StartAsync().ConfigureAwait(true);
        Boot("host started");

        // Open gates for Eager modules so their hosted services start working immediately.
        var gates = _host.Services.GetRequiredService<ModuleGates>();
        foreach (var module in modules)
        {
            var eager = shellSettings.ModuleEagerOverrides.TryGetValue(module.Id, out var v)
                ? v
                : module.DefaultActivation == ActivationMode.Eager;
            if (eager) gates.For(module.Id).Open();
        }

        // Construct shell window, attach hotkey hwnd
        Boot("resolving ShellWindow");
        var shell = _host.Services.GetRequiredService<ShellWindow>();
        Boot("resolving ShellViewModel");
        shell.DataContext = _host.Services.GetRequiredService<ShellViewModel>();
        Boot("shell DataContext set");
        shell.Left = shellSettings.WindowLeft;
        shell.Top = shellSettings.WindowTop;
        shell.Width = shellSettings.WindowWidth;
        shell.Height = shellSettings.WindowHeight;
        MainWindow = shell;

        // Wire view DataContexts
        _host.Services.GetRequiredService<GameConfigView>().DataContext = _host.Services.GetRequiredService<GameConfigViewModel>();
        _host.Services.GetRequiredService<HotkeyBindingsView>().DataContext = _host.Services.GetRequiredService<HotkeyBindingsViewModel>();

        Boot("calling shell.Show()");
        shell.Show();
        Boot("shell shown");
        var hwnd = new WindowInteropHelper(shell).Handle;
        var hk = _host.Services.GetRequiredService<IHotkeyService>();
        hk.Attach(hwnd);
        hk.ReloadFromBindings(shellSettings.HotkeyBindings.Values);
        Boot("=== startup done ===");
    }

    private async Task WatchActivateEvent(EventWaitHandle ev, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var signaled = await Task.Run(() => ev.WaitOne(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            if (!signaled) continue;
            await Dispatcher.InvokeAsync(() =>
            {
                if (MainWindow is null) return;
                if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
                MainWindow.Show();
                MainWindow.Activate();
                MainWindow.Topmost = true;
                MainWindow.Topmost = false;
            });
        }
    }

    private static void ShowFatal(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Gorgon", "Shell");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"\n=== {DateTime.Now:s} ===\n{ex}\n");
        }
        catch { }
        System.Windows.MessageBox.Show(ex.ToString(), "Gorgon crashed",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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
        try { _activateCts?.Cancel(); _activateCts?.Dispose(); } catch { }
        try { _activateEvent?.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
