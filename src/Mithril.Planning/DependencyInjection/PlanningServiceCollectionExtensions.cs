using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mithril.Leveling.DependencyInjection;
using Mithril.Shared.Crafting;

namespace Mithril.Planning.DependencyInjection;

public static class PlanningServiceCollectionExtensions
{
    /// <summary>
    /// Register the cross-skill leveling planner (<see cref="CrossSkillPlanner"/>).
    /// Pulls in <c>AddMithrilLeveling()</c> (the XP math, #225) and registers the
    /// shared recipe expander (#226). Idempotent — safe from any consumer (#228
    /// today, a planner UI follow-up later).
    /// </summary>
    public static IServiceCollection AddMithrilPlanning(this IServiceCollection services)
    {
        services.AddMithrilLeveling();
        services.TryAddSingleton<RecipeExpander>();
        services.TryAddSingleton<CrossSkillPlanner>();
        return services;
    }
}
