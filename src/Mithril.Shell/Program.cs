using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Mithril.Shared.Audio;
using Mithril.Shared.Character;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Game;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Icons;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Mithril.Shared.Wpf;
using Mithril.Shell.DependencyInjection;
using Mithril.Shell.Updates;
using Mithril.Shell.ViewModels;
using Mithril.Shell.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Velopack;

namespace Mithril.Shell;

public static class Program
{
    private const string MutexName = @"Global\Mithril.Shell.SingleInstance";
    private const string ActivateEventName = @"Global\Mithril.Shell.Activate";

    private static readonly string BootLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mithril", "Shell", "boot.log");

    /// <summary>
    /// Drop-off file the second instance uses to hand an activation URI (e.g. <c>mithril://item/X</c>)
    /// to the first instance. Read and deleted by <see cref="App"/> on each activate-event signal.
    /// </summary>
    public static readonly string ActivationUriPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mithril", "Shell", "activation.uri");

    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack hooks must run before ANY side-effecting code (mutex, file I/O, WPF init):
        // --veloapp-install / --veloapp-uninstall / --veloapp-updated / --veloapp-firstrun
        // are stripped from argv here, and install/uninstall variants call Environment.Exit
        // after handling. Anything that ran first would leave settings folders, boot logs,
        // or single-instance handles behind during a quiet install.
        VelopackApp.Build().Run();

        // Channel marker is embedded by MSBuild via -p:MithrilUpdateChannel=…; 'dev' for F5.
        // Probe once we're past the Velopack hooks but before we build the host, so a
        // missing .NET 10 runtime on framework-dependent installs surfaces as a friendly
        // dialog instead of a CLR-load crash.
        var channel = UpdateChannelInfo.FromEmbedded();
        if (channel.IsFrameworkDependent && !DesktopRuntimeIsAvailable())
        {
            ShowMissingRuntimeDialog();
            return;
        }

        Mutex? mutex = null;
        EventWaitHandle? activateEvent = null;
        CancellationTokenSource? activateCts = null;
        IHost? host = null;

        try
        {
            Boot("=== startup ===");

            // Extract an activation URI from argv if present. The OS passes it as the first
            // argument when launching via the registered custom scheme (mithril://…).
            var activationUri = ExtractActivationUri(args);

            // Single-instance guard
            mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                try
                {
                    // Hand the URI to the first instance before signalling, so its WatchActivateEvent
                    // loop has a file to read. Overwrite any previous drop-off.
                    if (activationUri is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(ActivationUriPath)!);
                        File.WriteAllText(ActivationUriPath, activationUri);
                    }
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
            var shellDir = Path.Combine(localApp, "Mithril", "Shell");
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
            var referenceCacheDir = Path.Combine(localApp, "Mithril", "Reference");
            var communityCalibrationCacheDir = Path.Combine(localApp, "Mithril", "Reference", "CommunityCalibration");
            var iconCacheDir = Path.Combine(localApp, "Mithril", "Icons");
            var charactersRootDir = Path.Combine(localApp, "Mithril", "characters");

            var builder = Host.CreateApplicationBuilder(args);
            var audioSettings = new AudioSettings { ConcurrentAlarms = shellSettings.ConcurrentAlarms };

            builder.Services
                .AddSingleton<ISettingsStore<ShellSettings>>(shellStore)
                .AddSingleton(shellSettings)
                .AddSingleton<IActiveCharacterPersistence>(shellSettings)
                .AddSingleton(audioSettings)
                .AddSingleton(gameConfig)
                .AddMithrilDiagnostics(logDir)
                .AddMithrilGameServices()
                .AddMithrilPerCharacterStorage(charactersRootDir)
                .AddMithrilReferenceData(referenceCacheDir)
                .AddMithrilCommunityCalibration(communityCalibrationCacheDir)
                .AddMithrilIcons(iconCacheDir)
                .AddMithrilHotkeys()
                .AddMithrilDialogs()
                .AddMithrilModuleGates()
                .AddMithrilModules()
                .AddMithrilAttention()
                .AddMithrilShellUpdates()
                .AddMithrilShellViews()
                .AddMithrilItemDetail()
                .AddMithrilIngredientSources();

            Boot($"modules discovered: {builder.Services.Count(d => d.ServiceType == typeof(IMithrilModule))}");
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
            app.DeepLinkRouter = host.Services.GetRequiredService<IDeepLinkRouter>();
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

            AudioPlayer.ConcurrentPlayback = audioSettings.ConcurrentAlarms;
            audioSettings.PropertyChanged += (_, ev) =>
            {
                if (ev.PropertyName == nameof(AudioSettings.ConcurrentAlarms))
                {
                    AudioPlayer.ConcurrentPlayback = audioSettings.ConcurrentAlarms;
                    shellSettings.ConcurrentAlarms = audioSettings.ConcurrentAlarms;
                }
            };

            Boot("=== startup done ===");

            // Cold-start deep-link: the user launched us via a mithril:// URI and we are the
            // first instance. Dispatch once the shell is live. The warm-activation path is
            // handled inside App.WatchActivateEvent via the activation.uri drop-off.
            if (activationUri is not null)
            {
                app.Dispatcher.BeginInvoke(() => app.DeepLinkRouter?.Handle(activationUri));
            }

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
                "Mithril", "Shell");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"\n=== {DateTime.Now:s} ===\n{ex}\n");
        }
        catch { }
        System.Windows.MessageBox.Show(ex.ToString(), "Mithril crashed",
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

    private static string? ExtractActivationUri(string[] args)
    {
        foreach (var a in args)
        {
            if (!string.IsNullOrEmpty(a) &&
                a.StartsWith(MithrilUriSchemeRegistrar.Scheme + ":", StringComparison.OrdinalIgnoreCase))
                return a;
        }
        return null;
    }

    // The framework-dependent SKU expects Microsoft.WindowsDesktop.App 10.* on the host.
    // Without it the CoreCLR error message is incomprehensible; do a cheap probe of
    // C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App and surface a download link.
    private const string DotnetDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";
    private const int RequiredMajorVersion = 10;

    private static bool DesktopRuntimeIsAvailable()
    {
        try
        {
            var roots = new[]
            {
                Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"),
            }.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p));

            foreach (var root in roots)
            {
                var dir = Path.Combine(root!, "shared", "Microsoft.WindowsDesktop.App");
                if (!Directory.Exists(dir)) continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    var dot = name.IndexOf('.');
                    if (dot <= 0) continue;
                    if (int.TryParse(name[..dot], out var major) && major >= RequiredMajorVersion)
                        return true;
                }
            }
            // Fallback: if the framework description says we are already loaded on
            // a >= net10 runtime, trust it. (This path won't normally hit because the
            // probe runs before host startup, but it covers self-contained-style hosts.)
            var fx = RuntimeInformation.FrameworkDescription;
            return fx.Contains(".NET 10", StringComparison.OrdinalIgnoreCase) ||
                   fx.Contains(".NET 11", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // If we can't probe, don't block launch — the user will see the CLR error and
            // we've at least tried to be helpful.
            return true;
        }
    }

    private static void ShowMissingRuntimeDialog()
    {
        var result = System.Windows.MessageBox.Show(
            "Mithril needs the .NET 10 Desktop Runtime, which doesn't appear to be installed on this machine.\n\n" +
            "Click OK to open the download page (look for \".NET Desktop Runtime 10.x\", x64).",
            "Missing .NET 10 Desktop Runtime",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.OK)
        {
            try { Process.Start(new ProcessStartInfo(DotnetDownloadUrl) { UseShellExecute = true }); }
            catch { /* best-effort */ }
        }
    }
}
