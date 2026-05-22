using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mithril.WorldSim.Chat.Internal;
using Mithril.WorldSim.Chat.Producers;

namespace Mithril.WorldSim.Chat.DependencyInjection;

/// <summary>
/// DI registration for the Phase 0 ChatWorld shell (issue #617, sibling of
/// <c>AddPlayerWorld</c> from #616). Registers the world singleton, the
/// chat-replay source + producer, the session service, and a
/// <see cref="BackgroundService"/> shim that calls <see cref="IWorld.StartAsync"/>
/// once the host boots. Per-folder migrations (#602 chat-inventory, #603
/// chat-WoP) wire their folder / composer registrations against the same
/// world singleton.
/// </summary>
public static class ChatWorldServiceCollectionExtensions
{
    /// <summary>
    /// Register the ChatWorld shell and bind it to a file-based
    /// <see cref="ChatLogReplaySource"/> over
    /// <see cref="Mithril.Shared.Game.GameConfig.ChatLogDirectory"/>. Must be
    /// called after <c>AddMithrilGameServices</c> (which registers
    /// <see cref="Mithril.Shared.Game.GameConfig"/> and the diagnostics sink).
    /// </summary>
    public static IServiceCollection AddChatWorld(this IServiceCollection services)
    {
        services
            .AddSingleton<ChatSessionService>()
            .AddSingleton<IChatSessionService>(sp => sp.GetRequiredService<ChatSessionService>())
            .AddSingleton<IChatLogReplaySource>(sp => new ChatLogReplaySource(
                config: sp.GetRequiredService<Mithril.Shared.Game.GameConfig>(),
                diag: sp.GetService<Mithril.Shared.Diagnostics.IDiagnosticsSink>(),
                time: sp.GetService<TimeProvider>()))
            .AddSingleton<ChatLogProducer>(sp => new ChatLogProducer(
                sp.GetRequiredService<IChatLogReplaySource>(),
                sp.GetRequiredService<ChatSessionService>()))
            .AddSingleton<ChatWorld>(sp =>
            {
                var world = new ChatWorld();
                world.RegisterProducer(sp.GetRequiredService<ChatLogProducer>());
                return world;
            })
            .AddSingleton<IChatWorld>(sp => sp.GetRequiredService<ChatWorld>())
            .AddHostedService<ChatWorldHostedService>();

        return services;
    }

    private sealed class ChatWorldHostedService : BackgroundService
    {
        private readonly ChatWorld _world;

        public ChatWorldHostedService(ChatWorld world) => _world = world;

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
            => _world.StartAsync(stoppingToken);
    }
}
