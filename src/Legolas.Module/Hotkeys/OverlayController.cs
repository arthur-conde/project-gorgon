using System.ComponentModel;
using System.Windows;
using Gorgon.Shared.Modules;
using Legolas.ViewModels;
using Legolas.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Legolas.Hotkeys;

/// <summary>
/// Manages the two topmost transparent overlay windows in response to
/// <see cref="SessionState.IsMapVisible"/> / <see cref="SessionState.IsInventoryVisible"/>.
/// Overlays cannot live inside the shell's ContentPresenter, so they stay as
/// top-level <see cref="Window"/>s owned by a module-scoped controller.
/// </summary>
public sealed class OverlayController : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ModuleGates _gates;
    private readonly SessionState _session;
    private readonly CancellationTokenSource _stopCts = new();
    private Task? _activationTask;
    private bool _subscribed;
    private MapOverlayView? _map;
    private InventoryOverlayView? _inventory;

    public OverlayController(IServiceProvider services, ModuleGates gates, SessionState session)
    {
        _services = services;
        _gates = gates;
        _session = session;
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
                    _subscribed = true;
                    SyncMap();
                    SyncInventory();
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
            if (_subscribed) _session.PropertyChanged -= OnSessionPropertyChanged;
            _map?.Close();
            _inventory?.Close();
            _map = null;
            _inventory = null;
        });
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionState.IsMapVisible)) SyncMap();
        else if (e.PropertyName == nameof(SessionState.IsInventoryVisible)) SyncInventory();
    }

    private void SyncMap()
    {
        if (_session.IsMapVisible) EnsureMap().Show();
        else _map?.Hide();
    }

    private void SyncInventory()
    {
        if (_session.IsInventoryVisible) EnsureInventory().Show();
        else _inventory?.Hide();
    }

    private MapOverlayView EnsureMap()
    {
        if (_map is not null) return _map;
        _map = _services.GetRequiredService<MapOverlayView>();
        _map.Closed += (_, _) =>
        {
            _map = null;
            _session.IsMapVisible = false;
        };
        return _map;
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
}
