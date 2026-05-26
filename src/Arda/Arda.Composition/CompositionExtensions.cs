using Arda.Composition.Internal;
using Arda.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mithril.Shared.Character;

namespace Arda.Composition;

public static class CompositionExtensions
{
    /// <summary>
    /// Register the Arda composition pipeline (L4 cross-source composers).
    /// Call after both <c>AddPlayerWorld()</c> and <c>AddChatWorld()</c>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="charactersRootDir">Root directory for per-character state files.
    /// When <c>null</c>, the inventory accumulator runs without persistence.</param>
    public static IServiceCollection AddArdaComposition(
        this IServiceCollection services,
        string? charactersRootDir = null)
    {
        services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventBus>();
            var loggerFactory = sp.GetService<ILoggerFactory>();

            PerCharacterStore<AccumulatorSnapshot>? store = null;
            if (!string.IsNullOrEmpty(charactersRootDir))
            {
                store = new PerCharacterStore<AccumulatorSnapshot>(
                    charactersRootDir,
                    "inventory-accumulator.json",
                    AccumulatorSnapshotJsonContext.Default.AccumulatorSnapshot,
                    logger: loggerFactory?.CreateLogger("PerCharacterStore<AccumulatorSnapshot>"));
            }

            return new InventoryComposer(bus, store,
                loggerFactory?.CreateLogger("InventoryComposer"));
        });
        services.AddSingleton<IInventoryAccumulatorState>(sp =>
            sp.GetRequiredService<InventoryComposer>());

        services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventBus>();
            return new SessionComposer(bus);
        });
        services.AddSingleton<ISessionComposer>(sp => sp.GetRequiredService<SessionComposer>());

        services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventBus>();
            return new WordOfPowerComposer(bus);
        });
        services.AddSingleton<IWordOfPowerComposer>(sp => sp.GetRequiredService<WordOfPowerComposer>());

        return services;
    }
}
