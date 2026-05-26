using Arda.Composition.Internal;
using Arda.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace Arda.Composition;

public static class CompositionExtensions
{
    /// <summary>
    /// Register the Arda composition pipeline (L4 cross-source composers).
    /// Call after both <c>AddPlayerWorld()</c> and <c>AddChatWorld()</c>.
    /// </summary>
    public static IServiceCollection AddArdaComposition(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventBus>();
            return new InventoryComposer(bus);
        });
        return services;
    }
}
