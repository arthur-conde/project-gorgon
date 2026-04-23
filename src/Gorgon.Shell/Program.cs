using System.IO;
using System.Windows;
using System.Windows.Interop;
using Gorgon.Shared.Character;
using Gorgon.Shared.DependencyInjection;
using Gorgon.Shared.Game;
using Gorgon.Shared.Hotkeys;
using Gorgon.Shared.Icons;
using Gorgon.Shared.Modules;
using Gorgon.Shared.Reference;
using Gorgon.Shared.Settings;
using Gorgon.Shared.Wpf;
using Gorgon.Shell.DependencyInjection;
using Gorgon.Shell.ViewModels;
using Gorgon.Shell.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Samwise.Alarms;

namespace Gorgon.Shell;

public static class Program
{
    private const string MutexName = @"Global\Gorgon.Shell.SingleInstance";
    private const string ActivateEventName = @"Global\Gorgon.Shell.Activate";

    private static readonly string BootLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Gorgon", "Shell", "boot.log");

    [STAThread]
    public static void Main(string[] args)
    {
        Mutex? mutex = null;
        EventWaitHandle? activateEvent = null;
        CancellationTokenSource? activateCts = null;
        IHost? host = null;

        try
        {
            Boot("=== startup ===");

            // Single-instance guard
            mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(ActivateEventName);
                    ev.Set();
                }
                catch { }
                return;
            }

            activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            activateCts = new CancellationTokenSource();

            // Shell settings — no SynchronizationContext yet, so blocking is safe
#pragma warning disable VSTHRD002 // runs before WPF dispatcher is installed
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var shellDir = Path.Combine(localApp, "Gorgon", "Shell");
            Directory.CreateDirectory(shellDir);
            var shellSettingsPath = Path.Combine(shellDir, "shell.json");

            var shellStore = new JsonSettingsStore<ShellSettings>(
                shellSettingsPath, ShellSettingsJsonContext.Default.ShellSettings);
            var shellSettings = shellStore.LoadAsync().GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(shellSettings.GameRoot))
                shellSettings.GameRoot = GameLocator.AutoDetectGameRoot() ?? "";

            // Game config (live, observable)
            var gameConfig = new GameConfig { GameRoot = shellSettings.GameRoot };
            gameConfig.PropertyChanged += (_, ev) =>
            {
                if (ev.PropertyName == nameof(GameConfig.GameRoot))
                    shellSettings.GameRoot = gameConfig.GameRoot;
            };

            // Build host
            var logDir = Path.Combine(shellDir, "logs");
            var referenceCacheDir = Path.Combine(localApp, "Gorgon", "Reference");
            var communityCalibrationCacheDir = Path.Combine(localApp, "Gorgon", "Reference", "CommunityCalibration");
            var iconCacheDir = Path.Combine(localApp, "Gorgon", "Icons");
            var charactersRootDir = Path.Combine(localApp, "Gorgon", "characters");

            var builder = Host.CreateApplicationBuilder(args);
            var audioSettings = new AudioSettings { ConcurrentAlarms = shellSettings.ConcurrentAlarms };

            builder.Services
                .AddSingleton<ISettingsStore<ShellSettings>>(shellStore)
                .AddSingleton(shellSettings)
                .AddSingleton<IActiveCharacterPersistence>(shellSettings)
                .AddSingleton(audioSettings)
                .AddSingleton(gameConfig)
                .AddGorgonDiagnostics(logDir)
                .AddGorgonGameServices()
                .AddGorgonPerCharacterStorage(charactersRootDir)
                .AddGorgonReferenceData(referenceCacheDir)
                .AddGorgonCommunityCalibration(communityCalibrationCacheDir)
                .AddGorgonIcons(iconCacheDir)
                .AddGorgonHotkeys()
                .AddGorgonDialogs()
                .AddGorgonModuleGates()
                .AddGorgonModules()
                .AddGorgonShellViews();

            Boot($"modules discovered: {builder.Services.Count(d => d.ServiceType == typeof(IGorgonModule))}");
            host = builder.Build();
            Boot("host built");
            host.StartAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            Boot("host started");

            // Persist active-character selection on every switch (log or UI).
            var activeCharSvc = host.Services.GetRequiredService<IActiveCharacterService>();
            activeCharSvc.ActiveCharacterChanged += (_, _) =>
            {
                try { shellStore.Save(shellSettings); } catch { /* best-effort */ }
            };

            host.Services.GetRequiredService<IReferenceDataService>().BeginBackgroundRefresh();

            // Community-calibration refresh is a single HTTP GET per module and fails gracefully
            // offline; always kick it. Per-module settings control merge behavior (PreferLocal /
            // Blend / PreferCommunity), not whether we fetch — users who want zero network can
            // firewall the host.
            host.Services.GetRequiredService<ICommunityCalibrationService>().BeginBackgroundRefresh();

            IconImage.SetCacheService(host.Services.GetRequiredService<IIconCacheService>());

            // Open gates for Eager modules
            var gates = host.Services.GetRequiredService<ModuleGates>();
            var discovered = host.Services.GetRequiredService<DiscoveredModules>();
            foreach (var module in discovered.Modules)
            {
                var eager = shellSettings.ModuleEagerOverrides.TryGetValue(module.Id, out var v)
                    ? v
                    : module.DefaultActivation == ActivationMode.Eager;
                if (eager) gates.For(module.Id).Open();
            }

            // Create and run WPF application
            Boot("creating App");
            var app = new App();
            app.Init(activateEvent, activateCts);
            app.InitializeComponent();

            _ = new UiFontApplier(app, shellSettings);

            Boot("resolving ShellWindow");
            var shell = host.Services.GetRequiredService<ShellWindow>();
            shell.DataContext = host.Services.GetRequiredService<ShellViewModel>();
            shell.Left = shellSettings.WindowLeft;
            shell.Top = shellSettings.WindowTop;
            shell.Width = shellSettings.WindowWidth;
            shell.Height = shellSettings.WindowHeight;
            app.MainWindow = shell;

            Boot("calling shell.Show()");
            shell.Show();
            shell.Closing += (_, _) =>
            {
                if (shell.WindowState == WindowState.Normal)
                {
                    shellSettings.WindowLeft = shell.Left;
                    shellSettings.WindowTop = shell.Top;
                    shellSettings.WindowWidth = shell.Width;
                    shellSettings.WindowHeight = shell.Height;
                }
            };
            Boot("shell shown");

            var hwnd = new WindowInteropHelper(shell).Handle;
            var hk = host.Services.GetRequiredService<IHotkeyService>();
            hk.Attach(hwnd);
            hk.ReloadFromBindings(shellSettings.HotkeyBindings.Values);

            AlarmSoundPlayer.ConcurrentPlayback = audioSettings.ConcurrentAlarms;
            audioSettings.PropertyChanged += (_, ev) =>
            {
                if (ev.PropertyName == nameof(AudioSettings.ConcurrentAlarms))
                {
                    AlarmSoundPlayer.ConcurrentPlayback = audioSettings.ConcurrentAlarms;
                    shellSettings.ConcurrentAlarms = audioSettings.ConcurrentAlarms;
                }
            };

            Boot("=== startup done ===");

            app.Run(); // blocks until WPF shuts down

            // Cleanup — save settings (position already captured in Closing handler)
            try
            {
                shellStore.Save(shellSettings);
            }
            catch { }
        }
        catch (Exception ex)
        {
            ShowFatal(ex);
        }
        finally
        {
            try { activateCts?.Cancel(); activateCts?.Dispose(); } catch { }
            try { activateEvent?.Dispose(); } catch { }
            if (host is not null)
            {
#pragma warning disable VSTHRD002 // shutdown path, no dispatcher running
                try { host.StopAsync(TimeSpan.FromSeconds(2)).Wait(TimeSpan.FromSeconds(3)); } catch { }
#pragma warning restore VSTHRD002
                try { host.Dispose(); } catch { }
            }
            try { mutex?.ReleaseMutex(); } catch { }
            mutex?.Dispose();
        }
    }

    internal static void ShowFatal(Exception ex)
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
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void Boot(string step)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BootLogPath)!);
            File.AppendAllText(BootLogPath, $"{DateTime.Now:HH:mm:ss.fff} {step}\n");
        }
        catch { }
    }
}
