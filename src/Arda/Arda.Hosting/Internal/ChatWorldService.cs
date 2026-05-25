using Arda.Dispatch;
using Arda.Ingest.Coordinator;
using Microsoft.Extensions.Hosting;

namespace Arda.Hosting.Internal;

/// <summary>
/// BackgroundService that drives the Chat log family through L2/L3 dispatch.
/// </summary>
internal sealed class ChatWorldService(
    ChatLogSource source,
    DispatchTable dispatchTable,
    ReplayProgress progress) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var driver = new WorldDriver(
            source,
            dispatchTable,
            onLiveTransition: () => progress.MarkComplete(ReplayProgress.SourceFamily.Chat));

        return driver.RunAsync(stoppingToken);
    }
}
