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
    /// Add Player-world state handlers (Map, Inventory, Player, Npc; future: Calendar).
    /// </summary>
    /// <param name="builder">The Arda builder from <c>AddArda()</c>.</param>
    /// <param name="itemPoolFactory">
    /// Optional factory for the item <see cref="InternPool"/>. When provided, the pool
    /// is seeded from reference data (e.g. <c>IReferenceDataService.ItemsByInternalName.Keys</c>)
    /// for true zero-alloc interning. When null, an empty pool is used (allocates on first
    /// encounter, caches for subsequent hits).
    /// </param>
    /// <param name="skillPoolFactory">
    /// Optional factory for the skill <see cref="InternPool"/>. When provided, the pool
    /// is seeded from reference data skill keys. When null, an empty pool is used
    /// (miss-cache handles the ~100 skill keys after first encounter).
    /// </param>
    public static ArdaBuilder AddPlayerWorld(
        this ArdaBuilder builder,
        Func<IServiceProvider, InternPool>? itemPoolFactory = null,
        Func<IServiceProvider, InternPool>? skillPoolFactory = null)
    {
        // --- Map handler ---
        // Area keys are few (~50) and stable; miss-cache-only interning is acceptable.
        // Each key allocates once on first encounter and is reused thereafter.
        var areaPool = new InternPool(FrozenDictionary<string, string>.Empty);

        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventBus>();
            return new Map(bus, areaPool);
        });
        builder.Services.AddSingleton<IAreaState>(sp => sp.GetRequiredService<Map>());

        // --- Inventory handler ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventBus>();
            var itemPool = itemPoolFactory?.Invoke(sp)
                ?? new InternPool(FrozenDictionary<string, string>.Empty);
            return new Inventory(bus, itemPool);
        });
        builder.Services.AddSingleton<IInventoryState>(sp => sp.GetRequiredService<Inventory>());

        // --- Player handler (skills + recipes) ---
        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventBus>();
            var skillPool = skillPoolFactory?.Invoke(sp)
                ?? new InternPool(FrozenDictionary<string, string>.Empty);
            return new Internal.Player(bus, skillPool);
        });
        builder.Services.AddSingleton<IPlayerState>(sp => sp.GetRequiredService<Internal.Player>());

        // --- Npc handler (interaction context + gift correlation) ---
        // NPC keys (~200) use miss-cache-only interning like area keys.
        var npcPool = new InternPool(FrozenDictionary<string, string>.Empty);

        builder.Services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventBus>();
            return new Npc(bus, npcPool);
        });
        builder.Services.AddSingleton<INpcState>(sp => sp.GetRequiredService<Npc>());

        // --- Dispatch table wiring ---
        builder.ConfigureHandlers((sp, registry) =>
        {
            var map = sp.GetRequiredService<Map>();
            RegisterHandler(registry, Verbs.LoadingLevel, map);
            RegisterHandler(registry, Verbs.InitializingArea, map);

            var inventory = sp.GetRequiredService<Inventory>();
            var player = sp.GetRequiredService<Internal.Player>();
            var npc = sp.GetRequiredService<Npc>();

            // Order matters: Map must run before StateResetHandler for LOADING_LEVEL.
            // Map clears CurrentArea; StateResetHandler then resets downstream state.
            // DispatchTable preserves insertion order within a verb's handler list.
            RegisterHandler(registry, Verbs.LoadingLevel, new StateResetHandler(inventory, player, npc));

            RegisterHandler(registry, Verbs.ProcessAddItem, new AddItemHandler(inventory));
            RegisterHandler(registry, Verbs.ProcessDeleteItem, new DeleteItemHandler(inventory));
            RegisterHandler(registry, Verbs.ProcessUpdateItemCode, new UpdateItemCodeHandler(inventory));

            RegisterHandler(registry, Verbs.ProcessLoadSkills, new LoadSkillsHandler(player));
            RegisterHandler(registry, Verbs.ProcessUpdateSkill, new UpdateSkillHandler(player));
            RegisterHandler(registry, Verbs.ProcessLoadRecipes, new LoadRecipesHandler(player));
            RegisterHandler(registry, Verbs.ProcessUpdateRecipe, new UpdateRecipeHandler(player));

            RegisterHandler(registry, Verbs.ProcessStartInteraction, new StartInteractionHandler(npc));
            RegisterHandler(registry, Verbs.ProcessDeleteItem, new NpcDeleteItemHandler(npc));
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
