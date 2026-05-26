using Arda.Dispatch;
using Arda.Ingest.Coordinator;
using Microsoft.Extensions.Hosting;

namespace Arda.Hosting.Internal;

/// <summary>
/// BackgroundService that drives the Player log family through L2/L3 dispatch.
/// </summary>
internal sealed class PlayerWorldService(
    PlayerLogSource source,
    DispatchTable dispatchTable,
    ReplayProgress progress,
    IReadOnlyList<ILineObserver> lineObservers) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var driver = new WorldDriver(
            source,
            dispatchTable,
            onLiveTransition: () => progress.MarkComplete(ReplayProgress.SourceFamily.Player),
            observers: lineObservers);

        return driver.RunAsync(stoppingToken);
    }
}
