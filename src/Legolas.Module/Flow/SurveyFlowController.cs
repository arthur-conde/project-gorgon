using Legolas.Domain;
using Legolas.ViewModels;

namespace Legolas.Flow;

/// <summary>
/// Phase a Survey-mode session can be in. See docs/agent-plans/legolas-state-machine.md
/// for the full diagram and rationale (notably the post-Optimize "no new surveys" rule
/// driven by the position-anchor constraint).
/// </summary>
public enum SurveyFlowState
{
    AwaitingPosition,
    Listening,
    AwaitingPin,
    Gathering,
    Done,
}

public sealed record SurveyTransition(
    SurveyFlowState From,
    SurveyFlowState To,
    string Trigger);

/// <summary>
/// State machine for Survey mode. Methods are the public transition API; direct
/// mutation of <see cref="SessionState.Surveys"/> / <see cref="SessionState.PendingSurvey"/>
/// outside this class is no longer supported (still possible at the language level,
/// but considered a bug — call a controller method instead).
/// </summary>
public sealed class SurveyFlowController
{
    private readonly SessionState _session;
    private readonly LegolasSettings _settings;

    public SurveyFlowController(SessionState session, LegolasSettings settings)
    {
        _session = session;
        _settings = settings;
        _session.AllCollected += OnAllCollected;
    }

    public SurveyFlowState CurrentState { get; private set; } = SurveyFlowState.AwaitingPosition;

    /// <summary>Fires after every successful state change.</summary>
    public event Action<SurveyTransition>? Transitioned;

    /// <summary>True when <see cref="OnSurveyDetected"/> would be accepted.</summary>
    public bool CanAcceptSurvey => CurrentState is SurveyFlowState.Listening or SurveyFlowState.AwaitingPin;

    /// <summary>True when <see cref="OptimizeRoute"/> would be accepted.</summary>
    public bool CanOptimize => CurrentState == SurveyFlowState.Listening && _session.Surveys.Count > 0;

    /// <summary>
    /// Confirms a player position has been set (caller has already updated the projector
    /// and <see cref="SessionState.PlayerPosition"/>). Transitions <c>AwaitingPosition →
    /// Listening</c>. No-op from other states.
    /// </summary>
    public void ConfirmPlayerPosition()
    {
        if (CurrentState != SurveyFlowState.AwaitingPosition)
        {
            _session.LastLogEvent = $"ConfirmPlayerPosition ignored — state is {CurrentState}";
            return;
        }
        TransitionTo(SurveyFlowState.Listening, nameof(ConfirmPlayerPosition));
    }

    /// <summary>
    /// Re-anchor request (e.g. user pressed "Set Player Position"). Goes back to
    /// <c>AwaitingPosition</c> from any state. Preserves <see cref="SessionState.Surveys"/>
    /// (their offsets remain valid; only the projector origin will change on the next
    /// <see cref="ConfirmPlayerPosition"/>). Clears any pending pin.
    /// </summary>
    public void RequestSetPlayerPosition()
    {
        _session.PendingSurvey = null;
        if (CurrentState == SurveyFlowState.AwaitingPosition) return;
        TransitionTo(SurveyFlowState.AwaitingPosition, nameof(RequestSetPlayerPosition));
    }

    /// <summary>
    /// A new <see cref="SurveyDetected"/> arrived. Sets <see cref="SessionState.PendingSurvey"/>
    /// and transitions to <c>AwaitingPin</c> from <c>Listening</c> or <c>AwaitingPin</c>
    /// (overwriting any prior pending — preserves pre-FSM behaviour). Dropped from
    /// <c>Gathering</c>/<c>Done</c>/<c>AwaitingPosition</c> per the position-anchor
    /// constraint (see plan doc).
    /// </summary>
    public void OnSurveyDetected(SurveyDetected sd)
    {
        if (!CanAcceptSurvey)
        {
            _session.LastLogEvent = $"Survey: {sd.Name} → ignored ({DescribeWhyDropped()})";
            return;
        }
        _session.PendingSurvey = sd;
        if (CurrentState != SurveyFlowState.AwaitingPin)
            TransitionTo(SurveyFlowState.AwaitingPin, nameof(OnSurveyDetected));
    }

    /// <summary>
    /// Caller has placed the pending pin (added it to <see cref="SessionState.Surveys"/>).
    /// Clears <see cref="SessionState.PendingSurvey"/> and returns to <c>Listening</c>.
    /// </summary>
    public void ConfirmPin()
    {
        if (CurrentState != SurveyFlowState.AwaitingPin)
        {
            _session.LastLogEvent = $"ConfirmPin ignored — state is {CurrentState}";
            return;
        }
        _session.PendingSurvey = null;
        TransitionTo(SurveyFlowState.Listening, nameof(ConfirmPin));
    }

    /// <summary>
    /// Survey came in with ≥2 corrections already on file → caller auto-placed the pin
    /// without entering <c>AwaitingPin</c>. Stays in <c>Listening</c>; no transition.
    /// Exists to keep symmetry with <see cref="OnSurveyDetected"/>: callers should
    /// invoke exactly one of the two for every detected survey, so the controller has
    /// a chance to log/diagnose.
    /// </summary>
    public void NoteAutoPlacedSurvey(SurveyDetected sd)
    {
        if (!CanAcceptSurvey)
        {
            _session.LastLogEvent = $"Survey: {sd.Name} → ignored ({DescribeWhyDropped()})";
            return;
        }
        // No transition; the auto-placement happened directly in Listening.
    }

    /// <summary>Listening → Gathering. Caller is responsible for actually computing the route.</summary>
    public void OptimizeRoute()
    {
        if (CurrentState != SurveyFlowState.Listening)
        {
            _session.LastLogEvent = $"OptimizeRoute ignored — state is {CurrentState}";
            return;
        }
        TransitionTo(SurveyFlowState.Gathering, nameof(OptimizeRoute));
    }

    /// <summary>
    /// Reset the session: clears surveys + pending pin, returns to either
    /// <c>Listening</c> (if a player position is still set) or <c>AwaitingPosition</c>.
    /// Preserves the projector anchor; caller is responsible for clearing the projector
    /// if they want a hard reset.
    /// </summary>
    public void Reset()
    {
        _session.ClearSurveys();
        _session.PendingSurvey = null;
        var target = _session.HasPlayerPosition
            ? SurveyFlowState.Listening
            : SurveyFlowState.AwaitingPosition;
        if (CurrentState != target)
            TransitionTo(target, nameof(Reset));
    }

    private void OnAllCollected()
    {
        if (CurrentState is not (SurveyFlowState.Listening or SurveyFlowState.Gathering))
            return;
        TransitionTo(SurveyFlowState.Done, nameof(OnAllCollected));
        if (_settings.AutoResetWhenAllCollected)
            Reset();
    }

    private void TransitionTo(SurveyFlowState next, string trigger)
    {
        var prev = CurrentState;
        if (prev == next) return;
        CurrentState = next;
        Transitioned?.Invoke(new SurveyTransition(prev, next, trigger));
    }

    private string DescribeWhyDropped() => CurrentState switch
    {
        SurveyFlowState.AwaitingPosition => "set player position first",
        SurveyFlowState.Gathering => "route in progress; reset to start a new session",
        SurveyFlowState.Done => "session done; reset to start a new session",
        _ => $"state is {CurrentState}",
    };
}
