using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Legolas.Domain;
using Legolas.ViewModels;

namespace Legolas.Flow;

/// <summary>
/// Phase a Survey-mode session can be in. #454 retired the relative-offset
/// model: <c>ProcessMapFx</c> targets are absolute and need no player anchor,
/// so the old <c>AwaitingPosition</c>/<c>Ready</c> anchor-bootstrap states are
/// gone. The map-click anchor + <see cref="SessionState.PlayerPosition"/>
/// survive but are now <b>Motherlode-only</b> (Survey never reads them).
/// </summary>
public enum SurveyFlowState
{
    Listening,
    Gathering,
    Done,
}

public sealed record SurveyTransition(
    SurveyFlowState From,
    SurveyFlowState To,
    string Trigger);

/// <summary>
/// State machine for Survey mode. Collapsed to
/// <c>Listening → OptimizeRoute → Gathering → Done → (auto)Reset →
/// Listening</c> by #454. Pins arrive absolute (no anchor); a new target
/// during <c>Gathering</c> is accepted (the old position-anchor "drop new
/// surveys" constraint is retired with the relative model). Calibration is
/// FSM-independent (per-area, persisted) — it is gated in the wizard
/// step-machine, not here.
/// </summary>
public sealed partial class SurveyFlowController : ObservableObject
{
    private readonly SessionState _session;
    private readonly LegolasSettings _settings;
    private readonly TimeProvider _clock;

    public SurveyFlowController(SessionState session, LegolasSettings settings, TimeProvider? clock = null)
    {
        _session = session;
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
        _session.AllCollected += OnAllCollected;
        _session.Surveys.CollectionChanged += OnSurveysChanged;
    }

    /// <summary>
    /// Bridges <see cref="SessionState.Surveys"/> mutations to FSM concerns:
    ///   * Stamps <see cref="SessionState.StartedAt"/> on the first pin of a
    ///     cycle (count 0→1 while <c>Listening</c>). After an auto-reset the
    ///     state returns to <c>Listening</c> and the next first pin re-stamps —
    ///     this is the second-run regression fix (no AwaitingPosition→Listening
    ///     edge to depend on any more).
    ///   * <see cref="CanOptimize"/> change-notify (its <c>Surveys.Count</c>
    ///     dependency isn't visible to the generator).
    /// </summary>
    private void OnSurveysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add
            && _session.Surveys.Count == 1
            && CurrentState == SurveyFlowState.Listening
            && _session.StartedAt is null)
        {
            _session.StartedAt = _clock.GetUtcNow();
        }
        OnPropertyChanged(nameof(CanOptimize));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseDescription))]
    [NotifyPropertyChangedFor(nameof(CanOptimize))]
    private SurveyFlowState _currentState = SurveyFlowState.Listening;

    /// <summary>Fires after every successful state change.</summary>
    public event Action<SurveyTransition>? Transitioned;

    /// <summary>Human-readable description for the in-app status strip.</summary>
    public string PhaseDescription => CurrentState switch
    {
        SurveyFlowState.Listening => "Listening for surveys",
        SurveyFlowState.Gathering => "Walk the route to each target",
        SurveyFlowState.Done => "All surveys collected",
        _ => "",
    };

    /// <summary>
    /// True when <see cref="OptimizeRoute"/> would be accepted: Listening with
    /// at least one pin. (Listening no longer structurally implies a non-empty
    /// list — there's no Ready→Listening-on-first-pin edge any more — so the
    /// count is checked explicitly.)
    /// </summary>
    public bool CanOptimize =>
        CurrentState == SurveyFlowState.Listening && _session.Surveys.Count > 0;

    /// <summary>Listening → Gathering. Caller computes the route. No-op unless
    /// Listening with pins.</summary>
    public void OptimizeRoute()
    {
        if (CurrentState != SurveyFlowState.Listening || _session.Surveys.Count == 0)
        {
            _session.LastLogEvent = $"OptimizeRoute ignored — state is {CurrentState}";
            return;
        }
        TransitionTo(SurveyFlowState.Gathering, nameof(OptimizeRoute));
    }

    /// <summary>
    /// Reset the session: clears surveys and returns to <c>Listening</c>
    /// (always — there is no anchor precondition any more). The next
    /// first-pin arrival re-stamps a fresh <see cref="SessionState.StartedAt"/>
    /// via <see cref="OnSurveysChanged"/>.
    /// </summary>
    public void Reset()
    {
        _session.ClearSurveys();
        if (CurrentState != SurveyFlowState.Listening)
            TransitionTo(SurveyFlowState.Listening, nameof(Reset));
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
}
