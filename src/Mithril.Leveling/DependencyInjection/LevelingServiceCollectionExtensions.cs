using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mithril.Leveling.DependencyInjection;

public static class LevelingServiceCollectionExtensions
{
    /// <summary>
    /// Register the shared skill-XP math (<see cref="LevelingMath"/>). Depends only on
    /// <c>IReferenceDataService</c>, which is registered by <c>AddMithrilReferenceData()</c>;
    /// safe to call from any module (Elrond today, the #227 planner next) — the singleton
    /// is idempotent under repeated registration via <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection)"/>.
    /// </summary>
    public static IServiceCollection AddMithrilLeveling(this IServiceCollection services)
    {
        services.TryAddSingleton<LevelingMath>();
        return services;
    }
}
