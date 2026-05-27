using Microsoft.Extensions.Logging;
using System.Windows;
using System.Windows.Threading;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Hotkeys;

namespace Mithril.Shell.Hotkeys;

/// <summary>
/// Emergency-exit hotkey. Triggers the same shutdown path as the tray "Exit"
/// menu — <see cref="System.Windows.Application.Shutdown()"/> — so the WPF
/// window-close chain runs (saves window position) and Program.cs's finally
/// block tears down the IHost, persists settings, and releases the
/// single-instance mutex. Marked diagnostic / developer-only because most
/// users should reach for the tray's Exit; the global hotkey is here for the
/// case where the UI is wedged or hidden. Ships unbound — the user picks a
/// combo from the Hotkeys settings under <c>Shell · Diagnostics</c>.
/// </summary>
public sealed class ForceQuitCommand : IHotkeyCommand
{
    private readonly ILogger _logger;

    public ForceQuitCommand(ILogger logger)
    {
        _logger = logger;
    }

    public string Id => "mithril.shell.force-quit";
    public string DisplayName => "Force quit Mithril";
    public string? Category => "Shell · Diagnostics";
    public HotkeyBinding? DefaultBinding => null;
    public bool RespectsFocusGate => false;
    public bool IsDeveloperOnly => true;

    public Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogDiagnosticWarn("Shell", "Force-quit hotkey triggered — shutting down.");

        var app = System.Windows.Application.Current;
        if (app is null) return Task.CompletedTask;

        if (app.Dispatcher.CheckAccess()) app.Shutdown();
        else app.Dispatcher.InvokeAsync(app.Shutdown, DispatcherPriority.Send);
        return Task.CompletedTask;
    }
}
