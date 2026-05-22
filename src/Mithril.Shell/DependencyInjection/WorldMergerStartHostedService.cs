using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mithril.WorldSim;

namespace Mithril.Shell.DependencyInjection;

/// <summary>
/// The trailing-registered <see cref="IHostedService"/> that starts each
/// registered world's merger drain (#696 Call 2). Appended LAST by
/// <see cref="ShellComposition.AddMithrilApp"/> via the internal
/// <see cref="WorldMergerStartServiceCollectionExtensions.AddWorldMergerStart"/>
/// so the hosted-services runner invokes its <see cref="StartAsync"/> AFTER
/// every other registration hosted service has completed its work (bus
/// subscriptions attached, folder/producer registrations closed).
///
/// <para>This service is deliberately <em>not</em> a
/// <see cref="BackgroundService"/> — its
/// <see cref="BackgroundService.ExecuteAsync"/> would be scheduled on the
/// thread pool at a non-deterministic moment relative to subsequent services'
/// <c>StartAsync</c> calls. That is the exact race #696 corrects. By
/// implementing <see cref="IHostedService"/> directly and being registered
/// trailing, <see cref="StartAsync"/> runs at a deterministic point in the
/// startup sequence: strictly after every preceding hosted service.</para>
///
/// <para><b>Lifecycle.</b> <see cref="StartAsync"/> calls
/// <see cref="IWorld.StartMerger"/> on each registered <see cref="IWorld"/>
/// WITHOUT awaiting the returned task — those tasks are the long-running
/// drains, captured here for graceful shutdown. <see cref="StartAsync"/>
/// returns immediately so host startup is non-blocking.
/// <see cref="StopAsync"/> cancels the linked token and waits for the drains
/// to complete (bounded by the host's stop-token grace period).</para>
/// </summary>
internal sealed class WorldMergerStartHostedService : IHostedService
{
    private readonly IReadOnlyList<IWorld> _worlds;
    private readonly List<Task> _mergerTasks = new();
    private readonly CancellationTokenSource _cts = new();

    public WorldMergerStartHostedService(IEnumerable<IWorld> worlds)
        => _worlds = worlds.ToArray();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var world in _worlds)
        {
            _mergerTasks.Add(world.StartMerger(_cts.Token));
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts.Cancel(); } catch { /* best-effort */ }
        try
        {
            if (_mergerTasks.Count > 0)
            {
                await Task.WhenAll(_mergerTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Either our cancellation propagated up (expected) or the host's
            // stop-token expired before drain finished — both are non-fatal at
            // shutdown.
        }
        catch
        {
            // Merger exceptions surfaced during shutdown must not block host
            // teardown — they're already swallowed in production today because
            // BackgroundService.ExecuteAsync runs fire-and-forget. Preserving
            // that behaviour here keeps shutdown deterministic.
        }
        finally
        {
            _cts.Dispose();
        }
    }
}

/// <summary>
/// Internal-only DI extension. The internal modifier signals to readers
/// "you probably shouldn't be calling this from production composition" —
/// production goes through <see cref="ShellComposition.AddMithrilApp"/>,
/// which appends this extension trailing the full shell registration.
///
/// <para>Test access is via <c>InternalsVisibleTo("Mithril.Shell.Tests")</c>
/// (see <c>Mithril.Shell.csproj</c>). Tests use this directly to compose
/// partial stacks with a live merger, e.g.
/// <c>services.AddPlayerWorld().AddWorldMergerStart()</c> for
/// "single world in isolation" lifecycle scenarios. The friend-assembly
/// approach was chosen over a parallel
/// <c>AddWorldMergerStartForTesting()</c> extension so production and test
/// composition share the same code path and can't drift silently
/// (#696 ratification, docs/world-simulator.md §Call 2).</para>
/// </summary>
internal static class WorldMergerStartServiceCollectionExtensions
{
    /// <summary>
    /// Append the trailing <see cref="WorldMergerStartHostedService"/> to
    /// the service collection. MUST be the LAST hosted-service registration
    /// in the composition for the trailing-start invariant to hold.
    /// </summary>
    internal static IServiceCollection AddWorldMergerStart(this IServiceCollection services)
        => services.AddHostedService<WorldMergerStartHostedService>();
}
