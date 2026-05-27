using Arda.Dispatch;
using Arda.Ingest.Coordinator;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arda.Hosting.Internal;

/// <summary>
/// BackgroundService that drives the Player log family through L2/L3 dispatch.
/// </summary>
internal sealed class PlayerWorldService(
    PlayerLogSource source,
    DispatchTable dispatchTable,
    ReplayProgress progress,
    IReadOnlyList<ILineObserver> lineObservers,
    ILogger<PlayerWorldService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Player world driver starting");
        try
        {
            var driver = new WorldDriver(
                source,
                dispatchTable,
                onLiveTransition: () => progress.MarkComplete(ReplayProgress.SourceFamily.Player),
                observers: lineObservers,
                logger: logger,
                sourceFamily: "Player");

            await driver.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Player world driver stopped");
            throw;
        }
    }
}
