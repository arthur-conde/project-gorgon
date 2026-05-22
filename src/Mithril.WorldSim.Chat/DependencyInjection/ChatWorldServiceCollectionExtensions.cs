using Microsoft.Extensions.DependencyInjection;
using Mithril.WorldSim.Chat.Internal;
using Mithril.WorldSim.Chat.Producers;

namespace Mithril.WorldSim.Chat.DependencyInjection;

/// <summary>
/// DI registration for the Phase 0 ChatWorld shell (issue #617, sibling of
/// <c>AddPlayerWorld</c> from #616). Registers the world singleton, the
/// chat-replay source + producer, and the session service. Per-folder
/// migrations (#602 chat-inventory, #603 chat-WoP) wire their folder /
/// composer registrations against the same world singleton.
///
/// <para><b>Merger start is OUT of this extension</b> (#696 Call 2). The
/// merger drain is started trailing the entire shell composition by
/// <c>Mithril.Shell.DependencyInjection.WorldMergerStartHostedService</c>,
/// appended LAST by <c>ShellComposition.AddMithrilApp</c>. That hosted
/// service resolves <see cref="IEnumerable{IWorld}"/> and calls
/// <see cref="IWorld.StartMerger"/> on each registered world, which is why
/// this extension registers the concrete world AS <see cref="IWorld"/> as
/// well as <see cref="IChatWorld"/>.</para>
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
            // Also register as IWorld so the trailing
            // WorldMergerStartHostedService (#696 Call 2) can resolve every
            // registered world via IEnumerable<IWorld>.
            .AddSingleton<IWorld>(sp => sp.GetRequiredService<ChatWorld>());

        return services;
    }
}
