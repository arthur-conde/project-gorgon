using System.ComponentModel;
using System.Windows;
using Mithril.Shared.Modules;
using Mithril.Shared.Settings;
using Legolas.Controls;
using Legolas.Domain;
using Legolas.Services;
using Legolas.ViewModels;
using Legolas.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mithril.Overlay;

namespace Legolas.Hotkeys;

/// <summary>
/// Manages the two topmost transparent overlay windows in response to
/// <see cref="SessionState.IsMapVisible"/> / <see cref="SessionState.IsInventoryVisible"/>.
/// Overlays cannot live inside the shell's ContentPresenter, so they stay as
/// top-level <see cref="Window"/>s owned by a module-scoped controller.
///
/// Visibility is gated by both the user's intent flag AND the in-app
/// foreground state from <see cref="ForegroundFocusGate"/> (issue #116) so
/// alt-tabbing to a browser doesn't leave the overlays floating over it.
/// The user's intent flag is never mutated here — only the rendered window.
/// </summary>
public sealed class OverlayController : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ModuleGates _gates;
    private readonly SessionState _session;
    private readonly LegolasSettings _settings;
    private readonly ForegroundFocusGate _focusGate;
    private readonly IOverlayWindow _overlayWindow;
    private readonly SettingsAutoSaver<LegolasSettings> _settingsSaver;
    private readonly CancellationTokenSource _stopCts = new();
    private Task? _activationTask;
    private bool _subscribed;
    private bool _sharedMapWired;
    // #835 step 6: MapOverlayView is no longer shown in production — the
    // shared IOverlayWindow.Window is the visible map overlay. The legacy
    // view's class file isn't deleted (step 7 owns that) but the field +
    // EnsureMap glue here is gone, and its production Show() site (was
    // SyncMap) routes to _overlayWindow.Window instead.
    private InventoryOverlayView? _inventory;
    private CalibrationOverlayView? _calibration;

    public OverlayController(
        IServiceProvider services,
        ModuleGates gates,
        SessionState session,
        LegolasSettings settings,
        ForegroundFocusGate focusGate,
        IOverlayWindow overlayWindow,
        SettingsAutoSaver<LegolasSettings> settingsSaver)
    {
        _services = services;
        _gates = gates;
        _session = session;
        _settings = settings;
        _focusGate = focusGate;
        _overlayWindow = overlayWindow;
        _settingsSaver = settingsSaver;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Don't block host startup on the module gate — Lazy modules stay
        // closed until the user clicks the tab. Wait on a background task.
        _activationTask = Task.Run(async () =>
        {
            try
            {
                await _gates.For("legolas").WaitAsync(_stopCts.Token).ConfigureAwait(false);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _session.PropertyChanged += OnSessionPropertyChanged;
                    _focusGate.PropertyChanged += OnFocusGatePropertyChanged;
                    _settings.PropertyChanged += OnSettingsPropertyChanged;
                    _subscribed = true;
                    SyncMap();
                    SyncInventory();
                    SyncCalibration();
                });
            }
            catch (OperationCanceledException) { }
        }, _stopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopCts.Cancel();
        if (_activationTask is not null) { try { await _activationTask.ConfigureAwait(false); } catch { } }
        if (Application.Current is null) return;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_subscribed)
            {
                _session.PropertyChanged -= OnSessionPropertyChanged;
                _focusGate.PropertyChanged -= OnFocusGatePropertyChanged;
                _settings.PropertyChanged -= OnSettingsPropertyChanged;
            }
            // The shared overlay window is owned by OverlayWindowService's
            // hosted-service lifecycle — DO NOT Close() it from here per
            // the IOverlayWindow.Window remarks (host owns teardown).
            // Hide is harmless and matches the legacy view's Close+null
            // semantics for the user-visible state.
            if (_sharedMapWired) _overlayWindow.Window.Hide();
            _inventory?.Close();
            _calibration?.Close();
            _inventory = null;
            _calibration = null;
        });
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionState.IsMapVisible)) SyncMap();
        else if (e.PropertyName == nameof(SessionState.IsInventoryVisible)) SyncInventory();
        else if (e.PropertyName == nameof(SessionState.IsCalibrationVisible)) SyncCalibration();
    }

    private void OnFocusGatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ForegroundFocusGate.IsInApp)) return;
        SyncMap();
        SyncInventory();
        SyncCalibration();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Toggling the auto-hide setting itself should immediately reflect on
        // current visibility — flipping it ON while a non-game window has focus
        // should hide the overlays right away, and flipping it OFF should
        // restore them.
        if (e.PropertyName != nameof(LegolasSettings.AutoHideOverlaysOnGameUnfocused)) return;
        SyncMap();
        SyncInventory();
        SyncCalibration();
    }

    private bool ShouldRender(bool sessionFlag) =>
        sessionFlag && (_focusGate.IsInApp || !_settings.AutoHideOverlaysOnGameUnfocused);

    private void SyncMap()
    {
        // #835 step 6: the shared Mithril.Overlay.IOverlayWindow.Window is
        // now the visible map overlay; MapOverlayView is no longer Show()n.
        // Legolas's WindowLayoutBinder + KeepTopmost + ClickThrough chrome
        // behaviors are applied to the shared window lazily on first
        // attach. Step 7 lifts those behaviors into Mithril.Overlay and
        // retires this Legolas-side glue.
        if (ShouldRender(_session.IsMapVisible))
        {
            EnsureSharedMapWired();
            _overlayWindow.Window.Show();
        }
        else
        {
            // First-time access to the shared window forces lazy construction;
            // guard so we don't materialise it just to hide it. The window
            // is wired on the first ShouldRender=true path above.
            if (_sharedMapWired) _overlayWindow.Window.Hide();
        }
    }

    /// <summary>One-shot attach of Legolas's window-level chrome behaviors
    /// (layout persistence, topmost re-assertion, click-through) to the
    /// shared <see cref="IOverlayWindow.Window"/>. Runs on the dispatcher
    /// (this method is called from <see cref="SyncMap"/> which itself runs
    /// from a Dispatcher.InvokeAsync). Idempotent &#8212; the
    /// <see cref="_sharedMapWired"/> latch guards against re-attaching.
    /// Step 7 owns the lift of <see cref="ClickThrough"/> /
    /// <see cref="WindowLayoutBinder"/> into Mithril.Overlay.</summary>
    private void EnsureSharedMapWired()
    {
        if (_sharedMapWired) return;
        var window = _overlayWindow.Window;
        // The shared window has a HeaderChrome border at the top (see
        // Mithril.Overlay/Internal/OverlayWindow.xaml). Click-through
        // carves out that header so the chip + drag handle stay reachable
        // when the body passes clicks through to the game. The carve-out
        // requires a FrameworkElement reference — we pick it up from the
        // visual tree by name.
        FrameworkElement? headerChrome = null;
        if (window.IsLoaded)
        {
            headerChrome = window.FindName("HeaderChrome") as FrameworkElement;
        }
        else
        {
            window.Loaded += (_, _) =>
            {
                headerChrome = window.FindName("HeaderChrome") as FrameworkElement;
                ApplySharedClickThrough(window, headerChrome);
            };
        }

        WindowLayoutBinder.Bind(window, _settings.MapOverlay, _settingsSaver.Touch);
        ClickThrough.KeepTopmost(window);
        ApplySharedClickThrough(window, headerChrome);

        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LegolasSettings.ClickThroughMap))
                ApplySharedClickThrough(window, headerChrome);
        };

        _sharedMapWired = true;
    }

    private void ApplySharedClickThrough(Window window, FrameworkElement? headerChrome)
    {
        // No calibration-phase override here — the calibration capture
        // mouse handlers live on MapOverlayView and don't translate to the
        // shared window in step 6 (step 7 lifts them). Honour the user's
        // ClickThroughMap setting verbatim; the headerChrome carve-out
        // keeps the chip + drag area reachable either way.
        ClickThrough.Apply(window, _settings.ClickThroughMap, headerChrome);
    }

    private void SyncInventory()
    {
        if (ShouldRender(_session.IsInventoryVisible)) EnsureInventory().Show();
        else _inventory?.Hide();
    }

    private void SyncCalibration()
    {
        if (ShouldRender(_session.IsCalibrationVisible)) EnsureCalibration().Show();
        else _calibration?.Hide();
    }

    private InventoryOverlayView EnsureInventory()
    {
        if (_inventory is not null) return _inventory;
        _inventory = _services.GetRequiredService<InventoryOverlayView>();
        _inventory.Closed += (_, _) =>
        {
            _inventory = null;
            _session.IsInventoryVisible = false;
        };
        return _inventory;
    }

    private CalibrationOverlayView EnsureCalibration()
    {
        if (_calibration is not null) return _calibration;
        _calibration = _services.GetRequiredService<CalibrationOverlayView>();
        _calibration.Closed += (_, _) =>
        {
            _calibration = null;
            _session.IsCalibrationVisible = false;
        };
        return _calibration;
    }
}
