using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Windows;
using Celebrimbor.ViewModels;
using Mithril.Planning;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;

namespace Celebrimbor.Services;

/// <summary>
/// Celebrimbor's implementation of <see cref="ISavedLevelingPlanImportTarget"/>
/// (#228 PR-B/B1). Deserializes the canonical plan JSON, upserts it into the
/// <see cref="LevelingPlanStore"/> by id, brings the Celebrimbor tab forward
/// and surfaces the imported plan in the Plans area. Mirrors
/// <see cref="CraftListImportTarget"/>: UI-thread marshalled, all failures
/// logged and swallowed (callers are module hand-off / file activation paths
/// that must not throw).
/// </summary>
public sealed class SavedLevelingPlanImportTarget : ISavedLevelingPlanImportTarget
{
    private readonly LevelingPlanStore _store;
    private readonly PlansViewModel _plans;
    private readonly IModuleActivator? _activator;
    private readonly ILogger? _logger;

    public SavedLevelingPlanImportTarget(
        LevelingPlanStore store,
        PlansViewModel plans,
        IModuleActivator? activator = null,
        ILogger? logger = null)
    {
        _store = store;
        _plans = plans;
        _activator = activator;
        _logger = logger;
    }

    public void ImportPlan(string planJson, string source)
    {
        if (string.IsNullOrWhiteSpace(planJson))
        {
            _logger?.LogDiagnosticInfo("Celebrimbor", $"Plan import from \"{source}\": empty payload, dropped.");
            return;
        }

        SavedLevelingPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize(planJson, SavedLevelingPlanJsonContext.Default.SavedLevelingPlan);
        }
        catch (JsonException ex)
        {
            _logger?.LogDiagnosticInfo("Celebrimbor", $"Plan import from \"{source}\": invalid JSON ({ex.Message}), dropped.");
            return;
        }

        if (plan is null || plan.Phases.Count == 0)
        {
            _logger?.LogDiagnosticInfo("Celebrimbor", $"Plan import from \"{source}\": no phases, dropped.");
            return;
        }

        Dispatch(() =>
        {
            _store.Upsert(plan);
            if (_activator is not null && !_activator.Activate("celebrimbor"))
                _logger?.LogDiagnosticInfo("Celebrimbor", "Plan import: module activator could not find 'celebrimbor'.");
            _plans.SurfaceImported(plan.Id);
            _logger?.LogDiagnosticInfo("Celebrimbor", $"Imported leveling plan \"{plan.Skill} → {plan.GoalLevel}\" from \"{source}\".");
        });
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.InvokeAsync(action);
    }
}
