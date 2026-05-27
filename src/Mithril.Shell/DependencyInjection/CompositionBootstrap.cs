using Arda.Composition;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mithril.Shell.DependencyInjection;

/// <summary>
/// Forces DI resolution of all L4 composers before the pipeline drivers start.
/// Composers subscribe to <c>IDomainEventSubscriber</c> in their constructors;
/// if they aren't alive when <c>PlayerWorldService</c> replays the login banner,
/// they miss <c>SessionEstablished</c> and never load persisted state from disk.
/// Registered before the Arda drivers so hosted-service startup order guarantees
/// all subscriptions are wired before any events fire.
/// </summary>
#pragma warning disable CS9113 // Parameters exist solely to force DI resolution
internal sealed class CompositionBootstrap(
    ISessionComposer session,
    IInventoryAccumulatorState inventory,
    IPlayerProgressionState progression,
    INpcStateTracker npcState,
    ILogger<CompositionBootstrap> logger) : IHostedService
#pragma warning restore CS9113
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "L4 composition bootstrap resolved: Session={HasSession}, Inventory={HasInventory}, Progression={HasProgression}, NpcState={HasNpc}",
            session is not null,
            inventory is not null,
            progression is not null,
            npcState is not null);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
