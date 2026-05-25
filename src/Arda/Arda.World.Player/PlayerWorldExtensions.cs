using System.Collections.Frozen;
using Arda.Dispatch;
using Arda.Hosting;
using Arda.World.Player.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Arda.World.Player;

/// <summary>
/// Registers the Player-world L3 handlers with the Arda dispatch pipeline.
/// </summary>
public static class PlayerWorldExtensions
{
    /// <summary>
    /// Add Player-world state handlers (Map, future: Inventory, Player, Npc, Calendar).
    /// </summary>
    public static ArdaBuilder AddPlayerWorld(this ArdaBuilder builder)
    {
        // Area intern pool — starts empty since no CDN area list exists yet.
        // Low cardinality (~50 area keys/session), so InternOrAllocate is fine:
        // first encounter allocates, subsequent hits return the cached instance.
        var areaIdentitySet = FrozenDictionary<string, string>.Empty;
        var areaPool = new InternPool(areaIdentitySet);

        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventBus>();
            return new Map(bus, areaPool);
        });
        builder.Services.AddSingleton<IAreaState>(sp => sp.GetRequiredService<Map>());

        builder.ConfigureHandlers((sp, registry) =>
        {
            var map = sp.GetRequiredService<Map>();

            if (!registry.TryGetValue(Verbs.LoadingLevel, out var loadingList))
            {
                loadingList = [];
                registry[Verbs.LoadingLevel] = loadingList;
            }
            loadingList.Add(map);

            if (!registry.TryGetValue(Verbs.InitializingArea, out var initList))
            {
                initList = [];
                registry[Verbs.InitializingArea] = initList;
            }
            initList.Add(map);
        });

        return builder;
    }
}
