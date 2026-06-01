using Arda.Abstractions.Diagnostics;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.Hosting.Internal;
using Arda.Ingest.Coordinator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        // Ingest sources (L0/L1) — pulse sink threads through so live-tail
        // poll iterations feed WorldHealth drift (#856).
        services.AddSingleton(sp => new PlayerLogSource(
            options.LogDirectory,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            pollInterval,
            sp.GetService<ILoggerFactory>()?.CreateLogger("Arda.Player"),
            sp.GetRequiredService<IIngestPulseSink>()));

        services.AddSingleton(sp => new ChatLogSource(
            chatDir,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            pollInterval,
            sp.GetService<ILoggerFactory>()?.CreateLogger("Arda.Chat"),
            sp.GetRequiredService<IIngestPulseSink>()));

        // Event bus (shared across both driver families). The composite interface
        // (IDomainEventBus, internal to Arda.Dispatch) is intentionally not
        // registered — external consumers depend on the narrow halves so the
        // type system enforces the pub/sub split.
        services.AddSingleton<DomainEventBus>();
        services.AddSingleton<IDomainEventSubscriber>(sp => sp.GetRequiredService<DomainEventBus>());
        services.AddSingleton<IDomainEventPublisher>(sp => sp.GetRequiredService<DomainEventBus>());

        // Grammar-break signal (one-shot, halts both drivers on grammar drift)
        services.AddSingleton<IGrammarBreakSignal>(sp =>
            new GrammarBreakSignal(sp.GetService<ILogger<GrammarBreakSignal>>()));

        // Runtime-only toggles derived from process env (read once at startup)
        var tolerant = string.Equals(
            Environment.GetEnvironmentVariable("MITHRIL_GRAMMAR_TOLERANT"),
            "1", StringComparison.Ordinal);
        services.AddSingleton(new ArdaRuntimeOptions(TolerantGrammar: tolerant));

        // TimeProvider — registered if the host hasn't already supplied one.
        services.AddSingleton(TimeProvider.System);

        // Replay progress (shared, bindable from WPF splash)
        services.AddSingleton(sp =>
            new ReplayProgress(sp.GetService<ILogger<ReplayProgress>>()));
        services.AddSingleton<IReplayProgress>(sp => sp.GetRequiredService<ReplayProgress>());

        // Tailer-poll pulse (#856): one singleton implements both read and
        // write sides. Ingest sources write via IIngestPulseSink (in
        // Arda.Abstractions); WorldHealthView reads via IIngestPulse (here).
        services.AddSingleton<IngestPulse>();
        services.AddSingleton<IIngestPulse>(sp => sp.GetRequiredService<IngestPulse>());
        services.AddSingleton<IIngestPulseSink>(sp => sp.GetRequiredService<IngestPulse>());

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
