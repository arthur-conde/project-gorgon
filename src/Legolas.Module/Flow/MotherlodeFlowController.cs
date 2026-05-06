using Legolas.ViewModels;

namespace Legolas.Flow;

/// <summary>
/// Phase a Motherlode-mode session can be in. Simpler than <see cref="SurveyFlowState"/>
/// because Motherlode distances are absolute — re-measuring after Optimize doesn't have
/// the relative-offset hazard that Survey does. The wizard may later want finer-grained
/// states (per-position / per-distance prompts); kept minimal here until that work lands.
/// </summary>
public enum MotherlodeFlowState
{
    Idle,
    Measuring,
    Optimized,
}

public sealed record MotherlodeTransition(
    MotherlodeFlowState From,
    MotherlodeFlowState To,
    string Trigger);

/// <summary>
/// State machine for Motherlode mode. Owns transitions only; the
/// <see cref="MotherlodeViewModel"/> still owns the position list, slot collection,
/// and trilateration math.
/// </summary>
public sealed class MotherlodeFlowController
{
    private readonly SessionState _session;

    public MotherlodeFlowController(SessionState session)
    {
        _session = session;
    }

    public MotherlodeFlowState CurrentState { get; private set; } = MotherlodeFlowState.Idle;

    public event Action<MotherlodeTransition>? Transitioned;

    /// <summary>True when a record-position / record-distance action would be accepted.</summary>
    public bool CanMeasure => CurrentState is MotherlodeFlowState.Idle or MotherlodeFlowState.Measuring;

    /// <summary>True when <see cref="OptimizeRoute"/> would do anything useful.</summary>
    public bool CanOptimize => CurrentState == MotherlodeFlowState.Measuring;

    /// <summary>
    /// Notifies the controller that a position or distance was just recorded. Promotes
    /// <c>Idle → Measuring</c>. From <c>Optimized</c>, demotes back to <c>Measuring</c>
    /// — re-measuring after optimize is allowed because Motherlode distances are
    /// absolute (no anchor invalidation).
    /// </summary>
    public void NoteMeasurement(string detail)
    {
        switch (CurrentState)
        {
            case MotherlodeFlowState.Idle:
                TransitionTo(MotherlodeFlowState.Measuring, $"NoteMeasurement({detail})");
                break;
            case MotherlodeFlowState.Optimized:
                TransitionTo(MotherlodeFlowState.Measuring, $"NoteMeasurement({detail}, re-measuring)");
                break;
            // Already Measuring: nothing to do.
        }
    }

    /// <summary>Measuring → Optimized. Caller computes the route.</summary>
    public void OptimizeRoute()
    {
        if (CurrentState != MotherlodeFlowState.Measuring)
        {
            _session.LastLogEvent = $"Motherlode OptimizeRoute ignored — state is {CurrentState}";
            return;
        }
        TransitionTo(MotherlodeFlowState.Optimized, nameof(OptimizeRoute));
    }

    /// <summary>Reset to <c>Idle</c>. Caller is responsible for clearing data.</summary>
    public void Reset()
    {
        if (CurrentState != MotherlodeFlowState.Idle)
            TransitionTo(MotherlodeFlowState.Idle, nameof(Reset));
    }

    private void TransitionTo(MotherlodeFlowState next, string trigger)
    {
        var prev = CurrentState;
        if (prev == next) return;
        CurrentState = next;
        Transitioned?.Invoke(new MotherlodeTransition(prev, next, trigger));
    }
}
