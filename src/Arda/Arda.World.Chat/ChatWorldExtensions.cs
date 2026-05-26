using Arda.Contracts;
using Arda.Dispatch;
using Arda.Hosting;
using Arda.World.Chat.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Arda.World.Chat;

/// <summary>
/// Registers the Chat-world L3 handlers with the Arda dispatch pipeline.
/// </summary>
public static class ChatWorldExtensions
{
    /// <summary>
    /// Add Chat-world state handlers (ChatSession, ChatInventory, ChatLine).
    /// </summary>
    public static ArdaBuilder AddChatWorld(this ArdaBuilder builder)
    {
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            return new ChatSession(bus);
        });
        builder.Services.AddSingleton<IChatSessionState>(sp => sp.GetRequiredService<ChatSession>());

        builder.ConfigureHandlers((sp, registry) =>
        {
            var session = sp.GetRequiredService<ChatSession>();
            RegisterHandler(registry, Verbs.ChatLoginBanner, session);

            var bus = sp.GetRequiredService<IDomainEventPublisher>();
            RegisterHandler(registry, Verbs.StatusInventory, new ChatInventory(bus));
            RegisterHandler(registry, Verbs.ChatPlayerLine, new ChatLine(bus));
        });

        return builder;
    }

    private static void RegisterHandler(
        Dictionary<string, List<IFrameHandler>> registry,
        string verb,
        IFrameHandler handler)
    {
        if (!registry.TryGetValue(verb, out var list))
        {
            list = [];
            registry[verb] = list;
        }
        list.Add(handler);
    }
}
