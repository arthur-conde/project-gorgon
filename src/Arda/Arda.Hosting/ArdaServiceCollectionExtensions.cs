using Arda.Dispatch;
using Arda.Hosting.Internal;
using Arda.Ingest.Coordinator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Arda.Hosting;

/// <summary>
/// DI composition entry point for the entire Arda pipeline (L0 through L3).
/// </summary>
public static class ArdaServiceCollectionExtensions
{
    /// <summary>
    /// Register Arda's ingest, dispatch, and hosting services. Call this once
    /// from the shell's composition root.
    /// <para>
    /// Use the returned <see cref="ArdaBuilder"/> to register frame handlers
    /// before the host starts.
    /// </para>
    /// </summary>
    public static ArdaBuilder AddArda(this IServiceCollection services, ArdaOptions options)
    {
        var pollInterval = options.PollInterval ?? TimeSpan.FromMilliseconds(250);
        var chatDir = options.ChatLogDirectory
            ?? Path.Combine(options.LogDirectory, "ChatLogs");

        // Ingest sources (L0/L1)
        services.AddSingleton(sp => new PlayerLogSource(
            options.LogDirectory,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            pollInterval));

        services.AddSingleton(sp => new ChatLogSource(
            chatDir,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            pollInterval));

        // Event bus (shared across both driver families)
        services.AddSingleton<DomainEventBus>();
        services.AddSingleton<IDomainEventBus>(sp => sp.GetRequiredService<DomainEventBus>());
        services.AddSingleton<IDomainEventSubscriber>(sp => sp.GetRequiredService<DomainEventBus>());
        services.AddSingleton<IDomainEventPublisher>(sp => sp.GetRequiredService<DomainEventBus>());

        // Replay progress (shared, bindable from WPF splash)
        services.AddSingleton<ReplayProgress>();
        services.AddSingleton<IReplayProgress>(sp => sp.GetRequiredService<ReplayProgress>());

        // Background services (L2 drivers)
        services.AddHostedService<PlayerWorldService>();
        services.AddHostedService<ChatWorldService>();

        var builder = new ArdaBuilder(services);

        // DispatchTable is registered as a deferred singleton — the builder
        // collects handler registrations and builds the table on first resolve.
        services.AddSingleton(sp => builder.BuildDispatchTable(sp));

        // Line observers are deferred the same way — world extensions call
        // AddLineObserver<T>() on the builder, and the list is resolved at
        // first inject into PlayerWorldService.
        services.AddSingleton<IReadOnlyList<ILineObserver>>(sp => builder.BuildLineObservers(sp));

        return builder;
    }
}
