using CommunityToolkit.Mvvm.ComponentModel;
using Legolas.Domain;
using Legolas.ViewModels;

namespace Legolas.Flow;

/// <summary>
/// Phase a Survey-mode session can be in. See docs/legolas-overview.md for the
/// full flow + invariants (post-Optimize "no new surveys" rule, anchor-becomes-
/// load-bearing-after-first-survey, etc.).
/// </summary>
public enum SurveyFlowState
{
    AwaitingPosition,
    Listening,
    Gathering,
    Done,
}

public sealed record SurveyTransition(
    SurveyFlowState From,
    SurveyFlowState To,
    string Trigger);

/// <summary>
/// State machine for Survey mode. Methods are the public transition API; direct
/// mutation of <see cref="SessionState.Surveys"/> outside this class is no longer
/// supported (still possible at the language level, but considered a bug — call a
/// controller method instead).
/// </summary>
public sealed partial class SurveyFlowController : ObservableObject
{
    private readonly SessionState _session;
    private readonly LegolasSettings _settings;

    public SurveyFlowController(SessionState session, LegolasSettings settings)
    {
        _session = session;
        _settings = settings;
        _session.AllCollected += OnAllCollected;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseDescription))]
    [NotifyPropertyChangedFor(nameof(CanAcceptSurvey))]
    [NotifyPropertyChangedFor(nameof(CanOptimize))]
    private SurveyFlowState _currentState = SurveyFlowState.AwaitingPosition;

    /// <summary>Fires after every successful state change.</summary>
    public event Action<SurveyTransition>? Transitioned;

    /// <summary>
    /// Human-readable description of the current state, suitable for the in-app
    /// status strip. The wizard view (when it lands) will use richer per-state
    /// templates; this is the dashboard-style fallback.
    /// </summary>
    public string PhaseDescription => CurrentState switch
    {
        SurveyFlowState.AwaitingPosition => "Click the map to set player position",
        SurveyFlowState.Listening => "Listening for surveys",
        SurveyFlowState.Gathering => "Walk to each target — new surveys will be ignored until reset",
        SurveyFlowState.Done => "All surveys collected",
        _ => "",
    };

    /// <summary>True when <see cref="NoteSurveyDetected"/> would be accepted.</summary>
    public bool CanAcceptSurvey => CurrentState is SurveyFlowState.Listening;

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
    /// <see cref="ConfirmPlayerPosition"/>).
    /// </summary>
    public void RequestSetPlayerPosition()
    {
        if (CurrentState == SurveyFlowState.AwaitingPosition) return;
        TransitionTo(SurveyFlowState.AwaitingPosition, nameof(RequestSetPlayerPosition));
    }

    /// <summary>
    /// A new <see cref="SurveyDetected"/> arrived. Caller (LogIngestionService) has
    /// already auto-placed the pin at the projected position. The controller's job
    /// is purely to surface the inventory overlay and to log/diagnose drops when
    /// the survey arrived in a state that doesn't accept new surveys (Gathering /
    /// Done / AwaitingPosition — the position-anchor constraint).
    /// </summary>
    public void NoteSurveyDetected(SurveyDetected sd)
    {
        if (!CanAcceptSurvey)
        {
            _session.LastLogEvent = $"Survey: {sd.Name} → ignored ({DescribeWhyDropped()})";
            return;
        }
        // Surface the inventory grid so the user can see which slot to pick next.
        // AutoOverlayCoordinator reacts to visibility changes for click-through.
        _session.IsInventoryVisible = true;
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
    /// Reset the session: clears surveys, returns to either <c>Listening</c> (if a
    /// player position is still set) or <c>AwaitingPosition</c>. Preserves the
    /// projector anchor; caller is responsible for clearing the projector if they
    /// want a hard reset.
    /// </summary>
    public void Reset()
    {
        _session.ClearSurveys();
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
        CurrentState = next; // generated setter fires PropertyChanged for state + dependents
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
