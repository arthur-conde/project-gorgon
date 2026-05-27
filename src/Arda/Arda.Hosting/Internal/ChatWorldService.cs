using Arda.Dispatch;
using Arda.Ingest.Coordinator;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arda.Hosting.Internal;

/// <summary>
/// BackgroundService that drives the Chat log family through L2/L3 dispatch.
/// </summary>
internal sealed class ChatWorldService(
    ChatLogSource source,
    DispatchTable dispatchTable,
    ReplayProgress progress,
    ILogger<ChatWorldService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Chat world driver starting");
        try
        {
            var driver = new WorldDriver(
                source,
                dispatchTable,
                onLiveTransition: () => progress.MarkComplete(ReplayProgress.SourceFamily.Chat),
                logger: logger,
                sourceFamily: "Chat");

            await driver.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Chat world driver stopped");
            throw;
        }
    }
}
