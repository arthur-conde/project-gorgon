using Arda.Contracts.State.Health;
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
    IGrammarBreakSignal grammarSignal,
    ArdaRuntimeOptions runtimeOptions,
    TimeProvider time,
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
                sourceFamily: "Player",
                grammarSignal: grammarSignal,
                time: time,
                tolerantGrammar: runtimeOptions.TolerantGrammar);

            await driver.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Player world driver stopped");
            throw;
        }
        catch (Exception ex)
        {
            // Anything escaping RunAsync that isn't shutdown — file-I/O blip,
            // bug in a non-grammar code path, or an OCE from a linked token
            // that wasn't the host stoppingToken — would otherwise terminate
            // this BackgroundService while the companion chat driver kept
            // running. Half-dead pipeline with no banner. Raise a synthetic
            // grammar break so the companion driver halts cooperatively and
            // the shell banner surfaces, then propagate so the host also sees
            // the failure.
            logger.LogError(ex, "Player world driver crashed unexpectedly");
            grammarSignal.Raise(new GrammarBreak(
                SourceFamily: "Player",
                Verb: "(driver crashed)",
                SourceLine: "",
                TokenExcerpt: "",
                ParserHint: $"{ex.GetType().Name}: {ex.Message}",
                At: time.GetUtcNow()));
            throw;
        }
    }
}
