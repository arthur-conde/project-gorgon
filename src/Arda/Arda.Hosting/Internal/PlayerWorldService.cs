using Arda.Dispatch;
using Arda.Ingest.Coordinator;
using Microsoft.Extensions.Hosting;

namespace Arda.Hosting.Internal;

/// <summary>
/// BackgroundService that drives the Player log family through L2/L3 dispatch.
/// Signals replay completion when IsReplay transitions to false.
/// </summary>
internal sealed class PlayerWorldService(
    PlayerLogSource source,
    DispatchTable dispatchTable,
    ReplayProgress progress) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var replaySignalled = false;

        await foreach (var line in source.Lines(stoppingToken))
        {
            if (!replaySignalled && !line.Metadata.IsReplay)
            {
                replaySignalled = true;
                progress.MarkComplete(ReplayProgress.SourceFamily.Player);
            }

            var logSpan = line.Log.AsSpan();
            var verbSpan = VerbExtractor.Extract(logSpan);
            dispatchTable.Dispatch(verbSpan, logSpan, line.Log, line.Metadata);
        }

        if (!replaySignalled)
            progress.MarkComplete(ReplayProgress.SourceFamily.Player);
    }
}
