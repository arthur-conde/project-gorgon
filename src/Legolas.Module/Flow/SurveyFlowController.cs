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
///
/// <para>#476 added <see cref="SettingPosition"/>: an <em>optional</em>
/// manual-override sub-state for the Survey player-GPS. It is not a bootstrap
/// — the auto tracker anchor remains the zero-click default; this is the
/// "correct a stale anchor" escape hatch (Option&#160;C). It is entered on
/// demand from <see cref="Listening"/> or <see cref="Gathering"/> and returns
/// to whichever of those it came from, so the normal flow is unchanged for
/// users who never invoke it.</para>
/// </summary>
public enum SurveyFlowState
{
    Listening,
    Gathering,
    Done,
    SettingPosition,
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
///
/// <para>#476 adds an optional <c>{Listening|Gathering} ⇄ SettingPosition</c>
/// detour: <see cref="RequestSetPosition"/> parks the current state and
/// switches to <see cref="SurveyFlowState.SettingPosition"/>;
/// <see cref="ConfirmPosition"/> / <see cref="CancelSetPosition"/> return to
/// it. The FSM only owns the <em>phase</em> — the actual manual pixel is
/// recorded by <c>MapOverlayViewModel</c> before it calls
/// <see cref="ConfirmPosition"/>. The auto tracker GPS is unaffected and
/// remains the default; this detour is the stale-anchor override only.</para>
/// </summary>
public sealed partial class SurveyFlowController : ObservableObject
{
    private readonly SessionState _session;
    private readonly LegolasSettings _settings;
    private readonly TimeProvider _clock;

    /// <summary>The state <see cref="RequestSetPosition"/> parked, so
    /// <see cref="ConfirmPosition"/>/<see cref="CancelSetPosition"/> can
    /// restore it. Only meaningful while <see cref="CurrentState"/> is
    /// <see cref="SurveyFlowState.SettingPosition"/>.</summary>
    private SurveyFlowState _returnState = SurveyFlowState.Listening;

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
    [NotifyPropertyChangedFor(nameof(IsSettingPosition))]
    private SurveyFlowState _currentState = SurveyFlowState.Listening;

    /// <summary>Fires after every successful state change.</summary>
    public event Action<SurveyTransition>? Transitioned;

    /// <summary>Human-readable description for the in-app status strip.</summary>
    public string PhaseDescription => CurrentState switch
    {
        SurveyFlowState.Listening => "Listening for surveys",
        SurveyFlowState.Gathering => "Walk the route to each target",
        SurveyFlowState.Done => "All surveys collected",
        SurveyFlowState.SettingPosition => "Click the map where you are",
        _ => "",
    };

    /// <summary>The state a <see cref="SurveyFlowState.SettingPosition"/>
    /// detour will return to. Lets the wizard keep its panel anchored to the
    /// originating step (Listening vs Gathering) during the override.</summary>
    public SurveyFlowState ReturnState => _returnState;

    /// <summary>True while the optional manual position-override detour
    /// (#476) is active.</summary>
    public bool IsSettingPosition => CurrentState == SurveyFlowState.SettingPosition;

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
    /// Enter the optional manual position-override detour (#476). Parks the
    /// current phase so <see cref="ConfirmPosition"/>/<see cref="CancelSetPosition"/>
    /// can restore it. Valid only from <c>Listening</c> or <c>Gathering</c>
    /// (the two phases where a "where am I?" correction is meaningful) — no-op
    /// otherwise, and an idempotent no-op if already setting position. The
    /// auto tracker GPS is untouched; this only overrides it on confirm.
    /// </summary>
    public void RequestSetPosition()
    {
        if (CurrentState is not (SurveyFlowState.Listening or SurveyFlowState.Gathering))
        {
            _session.LastLogEvent = $"Set position ignored — state is {CurrentState}";
            return;
        }
        _returnState = CurrentState;
        TransitionTo(SurveyFlowState.SettingPosition, nameof(RequestSetPosition));
    }

    /// <summary>
    /// Commit the manual override and return to the parked phase. The pixel
    /// itself is written to the session by <c>MapOverlayViewModel</c> before
    /// this call — the FSM only owns the phase. No-op unless currently
    /// <c>SettingPosition</c>.
    /// </summary>
    public void ConfirmPosition()
    {
        if (CurrentState != SurveyFlowState.SettingPosition) return;
        TransitionTo(_returnState, nameof(ConfirmPosition));
    }

    /// <summary>Abandon the detour and return to the parked phase without
    /// changing the anchor. No-op unless currently <c>SettingPosition</c>.</summary>
    public void CancelSetPosition()
    {
        if (CurrentState != SurveyFlowState.SettingPosition) return;
        TransitionTo(_returnState, nameof(CancelSetPosition));
    }

    /// <summary>
    /// Reset the session: clears surveys and returns to <c>Listening</c>
    /// (always — there is no anchor precondition any more). The next
    /// first-pin arrival re-stamps a fresh <see cref="SessionState.StartedAt"/>
    /// via <see cref="OnSurveysChanged"/>.
    ///
    /// <para>Fires <see cref="Transitioned"/> with trigger
    /// <c>"Reset"</c> unconditionally — including the
    /// <c>Listening → Listening</c> self-edge — so that session-bound
    /// listeners (notably <c>ItemCollectionTracker._pendingAdds</c>) get a
    /// consistent "start over" signal regardless of the prior phase. The
    /// other listeners (<c>LegolasReportService</c>,
    /// <c>LegolasWizardViewModel</c>) filter on <c>t.To == Done</c> so they
    /// are unaffected by the extra self-edge frame.</para>
    /// </summary>
    public void Reset()
    {
        _session.ClearSurveys();
        if (CurrentState != SurveyFlowState.Listening)
        {
            TransitionTo(SurveyFlowState.Listening, nameof(Reset));
        }
        else
        {
            // Idempotent self-edge: surfaces the Reset trigger for
            // session-bound listeners (e.g. ItemCollectionTracker's pending-Add
            // queue clear) even when the FSM was already in Listening.
            Transitioned?.Invoke(new SurveyTransition(
                SurveyFlowState.Listening, SurveyFlowState.Listening, nameof(Reset)));
        }
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
