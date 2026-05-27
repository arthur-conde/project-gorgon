using Arda.Contracts.State.Health;
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
    IGrammarBreakSignal grammarSignal,
    ArdaRuntimeOptions runtimeOptions,
    TimeProvider time,
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
                sourceFamily: "Chat",
                grammarSignal: grammarSignal,
                time: time,
                tolerantGrammar: runtimeOptions.TolerantGrammar);

            await driver.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Chat world driver stopped");
            throw;
        }
        catch (Exception ex)
        {
            // See PlayerWorldService for the rationale — a non-shutdown crash
            // here would leave the player driver running against a frozen
            // pipeline with no banner.
            logger.LogError(ex, "Chat world driver crashed unexpectedly");
            grammarSignal.Raise(new GrammarBreak(
                SourceFamily: "Chat",
                Verb: "(driver crashed)",
                SourceLine: "",
                TokenExcerpt: "",
                ParserHint: $"{ex.GetType().Name}: {ex.Message}",
                At: time.GetUtcNow()));
            throw;
        }
    }
}
