using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Legolas.Interop;
using Microsoft.Extensions.Hosting;
using Mithril.Shared.Modules;

namespace Legolas.Services;

/// <summary>
/// Tracks whether the foreground window belongs to Mithril or the Project
/// Gorgon game process. Exposes a single observable <see cref="IsInApp"/> bool
/// that <see cref="Hotkeys.OverlayController"/> ANDs with the user's
/// IsMapVisible / IsInventoryVisible flags so overlays vanish while the user
/// is in a browser, Discord, etc., and reappear (with prior intent preserved)
/// when they come back.
/// </summary>
public sealed class ForegroundFocusGate : IHostedService, INotifyPropertyChanged
{
    private readonly ModuleGates _gates;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly uint _ownPid;

    // Held to prevent GC of the delegate while the native hook is live.
    private User32Focus.WinEventProc? _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private Task? _activationTask;
    private bool _isInApp = true;

    public ForegroundFocusGate(ModuleGates gates)
    {
        _gates = gates;
        _ownPid = (uint)Environment.ProcessId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsInApp
    {
        get => _isInApp;
        private set
        {
            if (_isInApp == value) return;
            _isInApp = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInApp)));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Wait on the legolas module gate before installing the hook — there's
        // no overlay to gate before the user opens the Legolas tab, and we
        // don't want to claim a system-wide hook for an unused module.
        _activationTask = Task.Run(async () =>
        {
            try
            {
                await _gates.For("legolas").WaitAsync(_stopCts.Token).ConfigureAwait(false);
                if (Application.Current?.Dispatcher is { } dispatcher)
                {
                    await dispatcher.InvokeAsync(InstallHook);
                }
            }
            catch (OperationCanceledException) { }
        }, _stopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopCts.Cancel();
        if (_activationTask is not null) { try { await _activationTask.ConfigureAwait(false); } catch { } }
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            await dispatcher.InvokeAsync(UninstallHook);
        }
    }

    private void InstallHook()
    {
        if (_hookHandle != IntPtr.Zero) return;
        _hookProc = OnWinEvent;
        _hookHandle = User32Focus.SetWinEventHook(
            User32Focus.EVENT_SYSTEM_FOREGROUND,
            User32Focus.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _hookProc,
            idProcess: 0,
            idThread: 0,
            User32Focus.WINEVENT_OUTOFCONTEXT);

        // Hook delivery is event-driven, so seed the gate from the current
        // foreground window — otherwise IsInApp stays at its default until the
        // next focus change.
        EvaluateForeground(User32Focus.GetForegroundWindow());
    }

    private void UninstallHook()
    {
        if (_hookHandle == IntPtr.Zero) return;
        User32Focus.UnhookWinEvent(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _hookProc = null;
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        if (eventType != User32Focus.EVENT_SYSTEM_FOREGROUND) return;
        EvaluateForeground(hwnd);
    }

    private void EvaluateForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        IsInApp = IsForegroundInApp(hwnd);
    }

    private bool IsForegroundInApp(IntPtr hwnd)
    {
        User32Focus.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return _isInApp;
        if (pid == _ownPid) return true;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            // ProcessName excludes the .exe extension on Windows, matching
            // the canonical "ProjectGorgon" name.
            return string.Equals(proc.ProcessName, "ProjectGorgon", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Process exited between the hook firing and us looking it up,
            // or access denied (elevated process). Treat as out-of-app so
            // overlays hide rather than stick around incorrectly.
            return false;
        }
    }
}
