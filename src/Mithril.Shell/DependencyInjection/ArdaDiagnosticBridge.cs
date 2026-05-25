using Arda.Dispatch;
using Arda.World.Player.Events;
using Mithril.Shared.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shell.DependencyInjection;

/// <summary>
/// Bridges Arda domain events to the Mithril diagnostics view for end-to-end
/// observability. Subscribes to key domain events and writes them to
/// <see cref="IDiagnosticsSink"/>. Temporary scaffolding until
/// <c>ILogger</c>-to-diagnostics routing is wired system-wide.
/// </summary>
internal sealed class ArdaDiagnosticBridge : IHostedService
{
    private readonly IDomainEventBus _bus;
    private readonly IDiagnosticsSink _diag;
    private readonly List<IDisposable> _subscriptions = [];

    public ArdaDiagnosticBridge(IDomainEventBus bus, IDiagnosticsSink diag)
    {
        _bus = bus;
        _diag = diag;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(_bus.Subscribe<AreaChanged>(e =>
        {
            var replay = e.Metadata.IsReplay ? " [replay]" : "";
            _diag.Write(
                DiagnosticLevel.Info,
                "Arda.Map",
                $"Area changed: {e.PreviousArea ?? "(none)"} → {e.CurrentArea ?? "(none)"}{replay}");
        }));

        _subscriptions.Add(_bus.Subscribe<InventoryItemAdded>(e =>
        {
            var replay = e.Metadata.IsReplay ? " [replay]" : "";
            _diag.Write(
                DiagnosticLevel.Trace,
                "Arda.Inventory",
                $"+{e.InternalName} ({e.InstanceId}){replay}");
        }));

        _subscriptions.Add(_bus.Subscribe<InventoryItemRemoved>(e =>
        {
            var replay = e.Metadata.IsReplay ? " [replay]" : "";
            _diag.Write(
                DiagnosticLevel.Trace,
                "Arda.Inventory",
                $"-{e.InternalName} ({e.InstanceId}){replay}");
        }));

        _subscriptions.Add(_bus.Subscribe<InventoryItemUpdated>(e =>
        {
            var replay = e.Metadata.IsReplay ? " [replay]" : "";
            _diag.Write(
                DiagnosticLevel.Trace,
                "Arda.Inventory",
                $"~{e.InstanceId} stack {e.PreviousStackSize}→{e.NewStackSize}{replay}");
        }));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        return Task.CompletedTask;
    }
}
