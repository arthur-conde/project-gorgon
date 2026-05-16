using Mithril.Planning;

namespace Celebrimbor.ViewModels;

/// <summary>Resting status of a saved plan, as shown by the manager pill/glyph.</summary>
public enum SavedPlanStatus
{
    NeverWalked,
    InProgress,
    Stale,
    Done,
    Hypothetical,
}

/// <summary>
/// One row in the Plans manager (#228 PR-B/B1, frame 02). A read-only
/// projection of a <see cref="SavedLevelingPlan"/> plus a manager-supplied
/// <paramref name="isStale"/> flag (staleness needs the live character, which
/// the row itself doesn't know). Telemetry-derived fields (crafts walked, xp
/// banked) are intentionally absent — deferred with the craft-count tracker.
/// </summary>
public sealed class SavedPlanRowViewModel
{
    public SavedPlanRowViewModel(SavedLevelingPlan plan, bool isStale, string? skillDisplayName = null)
    {
        Plan = plan;
        var phaseCount = plan.Phases.Count;
        var done = phaseCount > 0 && plan.CurrentPhaseIndex >= phaseCount;

        Status = plan.Character is null && !done ? SavedPlanStatus.Hypothetical
            : done ? SavedPlanStatus.Done
            : isStale ? SavedPlanStatus.Stale
            : plan.CurrentPhaseIndex > 0 ? SavedPlanStatus.InProgress
            : SavedPlanStatus.NeverWalked;

        // Display the human skill name; the plan still stores the id-shaped key.
        Title = $"{skillDisplayName ?? plan.Skill}  {plan.StartLevel}→{plan.GoalLevel}";

        var span = phaseCount > 0
            ? $"{plan.Phases[0].RecipeName} → {plan.Phases[^1].RecipeName} path · "
            : "";
        Subtitle = $"{span}{phaseCount} phases · {plan.TotalCrafts} predicted crafts";

        CharacterLabel = plan.Character is { } c ? $"{c.Name} @ {c.Server}" : "hypothetical";

        var completedPhases = Math.Min(Math.Max(plan.CurrentPhaseIndex, 0), phaseCount);
        PhaseText = $"{completedPhases} / {phaseCount}";
        ProgressFraction = phaseCount == 0 ? 0d : (double)completedPhases / phaseCount;

        AgeText = Humanize(DateTimeOffset.Now - plan.CreatedAt);
        Glyph = Status switch
        {
            SavedPlanStatus.InProgress => "▸",   // ▸
            SavedPlanStatus.Stale => "⚠",        // ⚠
            SavedPlanStatus.Done => "✓",         // ✓
            _ => "○",                            // ○
        };
    }

    public SavedLevelingPlan Plan { get; }
    public string Id => Plan.Id;
    public SavedPlanStatus Status { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string CharacterLabel { get; }
    public bool IsHypothetical => Plan.Character is null;
    public string PhaseText { get; }
    public double ProgressFraction { get; }
    public string AgeText { get; }
    public string Glyph { get; }

    /// <summary>Does this row pass the active manager filter chip?</summary>
    public bool MatchesFilter(PlanFilter filter) => filter switch
    {
        PlanFilter.InProgress => Status == SavedPlanStatus.InProgress,
        PlanFilter.Stale => Status == SavedPlanStatus.Stale,
        PlanFilter.Done => Status == SavedPlanStatus.Done,
        _ => true,
    };

    /// <summary>Free-text search over skill, character and phase recipe names.</summary>
    public bool MatchesSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (Title.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        // Also match the id-shaped key so deep-link / advanced users can still find it.
        if (Plan.Skill.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (CharacterLabel.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return Plan.Phases.Any(p => p.RecipeName.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static string Humanize(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}

/// <summary>Manager filter chips (frame 02). "All" is the unfiltered default.</summary>
public enum PlanFilter
{
    All,
    InProgress,
    Stale,
    Done,
}
