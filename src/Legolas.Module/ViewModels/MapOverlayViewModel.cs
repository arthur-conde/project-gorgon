using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Mithril.GameState.Movement;

namespace Legolas.ViewModels;

public sealed partial class MapOverlayViewModel : ObservableObject
{
    private readonly SessionState _session;
    private readonly ICoordinateProjector _projector;
    private readonly IRouteOptimizer _optimizer;
    private readonly LegolasSettings? _settings;
    private readonly SurveyFlowController _surveyFlow;
    private readonly LegolasBrushes _brushes;
    private readonly PinCalibrationCoordinator? _pinCal;
    private readonly IPlayerPositionTracker? _positionTracker;
    private readonly IAreaCalibrationService? _areaCalibration;

    public MapOverlayViewModel(SessionState session, ICoordinateProjector projector, IRouteOptimizer optimizer, SurveyFlowController surveyFlow, LegolasBrushes brushes)
        : this(session, projector, optimizer, surveyFlow, brushes, settings: null) { }

    public MapOverlayViewModel(SessionState session, ICoordinateProjector projector, IRouteOptimizer optimizer, SurveyFlowController surveyFlow, LegolasBrushes brushes, LegolasSettings? settings, PinCalibrationCoordinator? pinCalibration = null, IPlayerPositionTracker? positionTracker = null, IAreaCalibrationService? areaCalibration = null)
    {
        _session = session;
        _projector = projector;
        _optimizer = optimizer;
        _surveyFlow = surveyFlow;
        _brushes = brushes;
        _settings = settings;
        _pinCal = pinCalibration;
        _positionTracker = positionTracker;
        _areaCalibration = areaCalibration;
        if (_pinCal is not null)
            _pinCal.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PinCalibrationCoordinator.IsPairing)
                                   or nameof(PinCalibrationCoordinator.IsDropping)
                                   or nameof(PinCalibrationCoordinator.IsArmed))
                {
                    OnPropertyChanged(nameof(IsCalibrationCapturing));
                    OnPropertyChanged(nameof(IsCalibrationDropping));
                }
                else if (e.PropertyName is nameof(PinCalibrationCoordinator.PromptText))
                {
                    OnPropertyChanged(nameof(CalibrationPrompt));
                }
                else if (e.PropertyName is nameof(PinCalibrationCoordinator.SelectedMarker))
                {
                    OnPropertyChanged(nameof(HasSelectedCalibrationMarker));
                }
            };
        if (_settings is not null)
        {
            _settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LegolasSettings.SurveyPinRadiusMetres))
                {
                    OnPropertyChanged(nameof(PinRadius));
                    OnPropertyChanged(nameof(PinDiameter));
                }
            };
        }

        _session.Surveys.CollectionChanged += OnSurveysCollectionChanged;
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionState.PlayerPosition))
            {
                OnPropertyChanged(nameof(PlayerPosition));
                OnPropertyChanged(nameof(PlayerMarkerPixel));
                RebuildRouteGeometry();
                RebuildAllWedges();
            }
            else if (e.PropertyName is nameof(SessionState.HasPlayerPosition))
            {
                OnPropertyChanged(nameof(PlayerMarkerPixel));
            }
            else if (e.PropertyName is nameof(SessionState.SurveyPlayerPixel))
            {
                // #476: the Survey GPS moved (zone-in / teleport / calibration
                // (re)applied). It is the route start + the rendered marker +
                // the pre-first-collection guidance segment, so rebuild all
                // three.
                OnPropertyChanged(nameof(PlayerMarkerPixel));
                RebuildRouteGeometry();
            }
            else if (e.PropertyName is nameof(SessionState.SurveyPlayerMeasuredAt)
                     or nameof(SessionState.SurveyPlayerSource)
                     or nameof(SessionState.SurveyPlayerIsManual))
            {
                OnPropertyChanged(nameof(PlayerAnchorStatus));
                OnPropertyChanged(nameof(IsPlayerAnchorStatusVisible));
            }
            else if (e.PropertyName is nameof(SessionState.ShowRouteLines))
            {
                RebuildRouteGeometry();
                RebuildAllWedges();
            }
            else if (e.PropertyName is nameof(SessionState.ShowBearingWedges))
            {
                RebuildAllWedges();
            }
            else if (e.PropertyName is nameof(SessionState.Mode))
            {
                // Switching between Survey and Motherlode flips wedge
                // visibility wholesale — Survey hides them, Motherlode shows —
                // and swaps which player pixel the marker reads (#476).
                OnPropertyChanged(nameof(PlayerMarkerPixel));
                OnPropertyChanged(nameof(PlayerAnchorStatus));
                OnPropertyChanged(nameof(IsPlayerAnchorStatusVisible));
                RebuildAllWedges();
            }
        };

        // Forward FSM state changes so the pin DataTemplate can gate the
        // active-pin halo on Listening (the only phase where SelectedSurvey
        // is meaningful — Gathering uses IsActiveTarget + marching ants).
        _surveyFlow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SurveyFlowController.CurrentState))
            {
                OnPropertyChanged(nameof(IsListening));
                OnPropertyChanged(nameof(IsSettingPosition));
                OnPropertyChanged(nameof(OverlayHint));
                OnPropertyChanged(nameof(IsOverlayHintVisible));
                SetPositionCommand.NotifyCanExecuteChanged();
                CancelSetPositionCommand.NotifyCanExecuteChanged();
            }
        };

        // #476: Survey player-GPS. The tracker fix and the area calibration
        // are two independent inputs to the same projection — either changing
        // re-resolves the anchor. Subscribe replays Current synchronously, so
        // a fix already seen at startup is picked up immediately; the
        // calibration Changed event covers the (common) case where the area's
        // calibration is applied after the overlay VM is constructed.
        if (_positionTracker is not null && _areaCalibration is not null)
        {
            _positionTracker.Subscribe(_ => PostToUi(() => RefreshSurveyPlayerAnchor(fromTrackerFix: true)));
            _areaCalibration.Changed += (_, _) => PostToUi(() => RefreshSurveyPlayerAnchor(fromTrackerFix: false));
            RefreshSurveyPlayerAnchor(fromTrackerFix: false);
        }
    }

    /// <summary>
    /// Re-project the tracker's last world fix to a pixel through the current
    /// area's calibration and publish it (plus its age/source) onto the
    /// session. No tracker fix or no calibrated area ⇒ clear it (degrade
    /// silently — same "no marker" behaviour as before #476). The projection
    /// is <see cref="AreaCalibration.ProjectWorld"/> — the exact transform the
    /// <c>ProcessMapFx</c> pins use, so the marker lands in the same frame as
    /// the pins (subject to the ±10% non-affine map ceiling — it is "near you",
    /// not pixel-exact, and that is expected).
    ///
    /// <para>#476 Option&#160;C — manual-override interaction:
    /// <list type="bullet">
    /// <item><paramref name="fromTrackerFix"/> = a genuinely new fix
    /// (zone-in / teleport). Fresh data is authoritative again, so it
    /// supersedes a manual override (the override only existed to fix a
    /// <em>stale</em> anchor).</item>
    /// <item><paramref name="fromTrackerFix"/> = false (calibration
    /// re-applied). A manual override is a raw screen pixel that does not
    /// depend on the calibration, so leave it untouched; otherwise
    /// re-project auto.</item>
    /// </list></para>
    /// </summary>
    private void RefreshSurveyPlayerAnchor(bool fromTrackerFix)
    {
        if (_session.SurveyPlayerIsManual && !fromTrackerFix)
            return; // a deliberate correction outlives a calibration re-apply

        var fix = _positionTracker?.Current;
        var cal = _areaCalibration?.CurrentCalibration;
        if (fix is null || cal is null)
        {
            // Either no anchor possible (no fix / uncalibrated, not manual),
            // or a fresh tracker fix that supersedes a manual override but
            // can't be projected (uncalibrated area) — the player moved, so
            // the stale manual pixel is wrong now regardless. Clear everything.
            _session.SurveyPlayerPixel = null;
            _session.SurveyPlayerMeasuredAt = null;
            _session.SurveyPlayerSource = null;
            _session.SurveyPlayerIsManual = false;
            return;
        }

        _session.SurveyPlayerPixel = cal.ProjectWorld(new WorldCoord(fix.X, fix.Y, fix.Z));
        _session.SurveyPlayerMeasuredAt = fix.MeasuredAt;
        _session.SurveyPlayerSource = fix.Source;
        _session.SurveyPlayerIsManual = false;
    }

    /// <summary>
    /// Record the user's "set my position" map click (#476 Option&#160;C,
    /// the stale-anchor override). A raw screen pixel — independent of the
    /// area calibration, no log <c>Source</c>, stamped with the click time —
    /// that wins over the projected auto anchor until the next fresh tracker
    /// fix (zone-in / teleport) takes over again.
    /// </summary>
    private void RecordManualPosition(PixelPoint where)
    {
        _session.SurveyPlayerPixel = where;
        _session.SurveyPlayerMeasuredAt = DateTimeOffset.UtcNow;
        _session.SurveyPlayerSource = null;
        _session.SurveyPlayerIsManual = true;
    }

    /// <summary>
    /// Marshal to the WPF dispatcher — the tracker fires from the Player.log
    /// ingestion thread and we mutate observable session state bound to the
    /// overlay. Falls back to a direct call in headless/test contexts. Mirrors
    /// <c>PlayerLogIngestionService.PostToUi</c>.
    /// </summary>
    private static void PostToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) action();
        else dispatcher.InvokeAsync(action);
    }

    /// <summary>True iff the survey FSM is in <c>Listening</c>.</summary>
    public bool IsListening => _surveyFlow.CurrentState == SurveyFlowState.Listening;

    /// <summary>#476 Option&#160;C: true while the optional manual
    /// position-override detour is active — the overlay routes the next
    /// viewport click to <see cref="RecordManualPosition"/> and the wizard
    /// shows the cancel affordance.</summary>
    public bool IsSettingPosition => _surveyFlow.CurrentState == SurveyFlowState.SettingPosition;

    /// <summary>Enter the manual position-override detour (#476). Enabled
    /// only from Listening/Gathering — the FSM guards it too, but gating the
    /// command keeps the button disabled rather than a silent no-op.</summary>
    [RelayCommand(CanExecute = nameof(CanSetPosition))]
    private void SetPosition() => _surveyFlow.RequestSetPosition();

    private bool CanSetPosition() =>
        _surveyFlow.CurrentState is SurveyFlowState.Listening or SurveyFlowState.Gathering;

    /// <summary>Abandon the detour without changing the anchor (#476).</summary>
    [RelayCommand(CanExecute = nameof(IsSettingPosition))]
    private void CancelSetPosition() => _surveyFlow.CancelSetPosition();

    /// <summary>#460/#477A: true while the guided calibration walkthrough is in
    /// its <see cref="CalibrationPhase.Pair"/> phase — the overlay captures
    /// left-clicks (pair the named pin / select+drag a marker), so it must NOT
    /// be click-through. The view routes viewport clicks to
    /// <see cref="PairCalibrationClick"/> / marker selection while this holds.</summary>
    public bool IsCalibrationCapturing => _pinCal?.IsPairing == true;

    /// <summary>#477A: true while the walkthrough is in
    /// <see cref="CalibrationPhase.Drop"/> — the overlay must be click-through
    /// so right-clicks reach the game to drop pins. Drives the view's
    /// phase-aware click-through override (the panel button toggles the phase,
    /// not the overlay directly — the separate-window assumption).</summary>
    public bool IsCalibrationDropping => _pinCal?.IsDropping == true;

    /// <summary>Pair a calibration overlay-click with the currently-named
    /// (suggested/overridden) pin. No-op when not in the Pair phase.</summary>
    public void PairCalibrationClick(PixelPoint pixel) => _pinCal?.PairClick(pixel);

    /// <summary>Mouse-down hit-test against placed calibration markers
    /// (select-then-drag correction). False ⇒ the click should pair instead.</summary>
    public bool TrySelectCalibrationMarkerAt(PixelPoint at, double radius) =>
        _pinCal?.TrySelectMarkerAt(at, radius) == true;

    /// <summary>Drag the selected calibration marker to an absolute pixel.</summary>
    public void DragCalibrationMarkerTo(PixelPoint at) => _pinCal?.DragSelectedTo(at);

    /// <summary>True iff a calibration marker is currently selected (so the
    /// nudge keys/pad target it ahead of survey pins / the manual anchor).</summary>
    public bool HasSelectedCalibrationMarker => _pinCal?.SelectedMarker is not null;

    /// <summary>Deselect any calibration marker (Escape, or starting a fresh
    /// pair). No-op without a coordinator.</summary>
    public void ClearCalibrationSelection() => _pinCal?.ClearSelection();

    /// <summary>The guided walkthrough's current on-overlay prompt (names the
    /// pin to click next, etc.). Empty without a coordinator.</summary>
    public string CalibrationPrompt => _pinCal?.PromptText ?? string.Empty;

    /// <summary>Click-paired calibration markers to render on the overlay
    /// (null when no coordinator — e.g. the test ctor).</summary>
    public System.Collections.ObjectModel.ObservableCollection<CalibrationMarker>? CalibrationMarkers
        => _pinCal?.PlacedMarkers;

    /// <summary>
    /// Move the currently-nudgeable target by <c>(dx, dy) * step</c>. Precedence:
    /// <list type="number">
    /// <item>a selected <b>calibration marker</b> (#477A — the guided
    /// walkthrough's just-placed/selected marker, correcting the dominant
    /// click-precision error);</item>
    /// <item>the selected <see cref="SessionState.SelectedSurvey"/> pin
    /// (a survey still wins over the manual anchor);</item>
    /// <item>the <b>manual</b> Survey player anchor (#477C) — only when no
    /// survey is selected and <see cref="SessionState.SurveyPlayerIsManual"/>;
    /// the auto/tracker-projected anchor is intentionally non-interactive
    /// (nudging a data-sourced fix would mask staleness).</item>
    /// </list>
    /// No-op otherwise. Shared by the keyboard hotkeys (NudgePinCommandBase)
    /// and the on-screen nudge pad.
    /// </summary>
    public void Nudge(double dx, double dy, double step)
    {
        if (_pinCal?.SelectedMarker is not null)
        {
            _pinCal.NudgeSelected(dx * step, dy * step);
            return;
        }

        var selected = _session.SelectedSurvey;
        if (selected is not null && selected.EffectivePixel.HasValue)
        {
            var p = selected.EffectivePixel.Value;
            CorrectSurveyCommand.Execute(
                new CorrectionArgs(selected, new PixelPoint(p.X + dx * step, p.Y + dy * step)));
            return;
        }

        // #477C: the manual "Set my position" anchor is selectable/nudgeable on
        // this same shared layer. Mutate only SurveyPlayerPixel and keep the
        // manual flag (a fresh tracker fix still supersedes it per #476); never
        // touch the Motherlode PlayerPosition or the retired MoveAnchor model.
        if (_session.Mode == SessionMode.Survey
            && _session.SurveyPlayerIsManual
            && _session.SurveyPlayerPixel is { } anchor)
        {
            _session.SurveyPlayerPixel =
                new PixelPoint(anchor.X + dx * step, anchor.Y + dy * step);
            _session.SurveyPlayerIsManual = true;
        }
    }

    private void OnSurveysCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (SurveyItemViewModel s in e.NewItems)
            {
                s.PropertyChanged += OnSurveyPropertyChanged;
            }
        }
        if (e.OldItems is not null)
        {
            foreach (SurveyItemViewModel s in e.OldItems)
            {
                s.PropertyChanged -= OnSurveyPropertyChanged;
            }
        }
        RebuildRouteGeometry();
        RebuildAllWedges();
    }

    private void OnSurveyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SurveyItemViewModel.Model))
        {
            RebuildRouteGeometry();
            if (sender is SurveyItemViewModel s) RebuildWedgeFor(s);
        }
        else if (e.PropertyName is nameof(SurveyItemViewModel.IsActiveTarget))
        {
            // Active-target change rotates the live segment without changing
            // the rest of the route, so don't churn the full polyline.
            RebuildActiveSegment();
        }
    }

    public ObservableCollection<SurveyItemViewModel> Surveys => _session.Surveys;

    public SessionState Session => _session;

    public SurveyFlowController SurveyFlow => _surveyFlow;

    public LegolasBrushes Brushes => _brushes;

    private static readonly LegolasPinStyle _defaultPinStyle = new();
    private static readonly LegolasPinStyle _defaultPlayerPinStyle = LegolasPinStyle.PlayerDefaults();
    private static readonly LegolasPinStyle _defaultCalibrationPinStyle = LegolasPinStyle.CalibrationDefaults();
    private static readonly LegolasActivePinStyle _defaultActivePinStyle = new();

    /// <summary>Survey pin shape configuration for the DataTemplate. Falls
    /// back to defaults when the simpler test constructor is used (no settings).</summary>
    public LegolasPinStyle PinStyle => _settings?.PinStyle ?? _defaultPinStyle;

    /// <summary>Player anchor pin shape configuration. Same shape model as
    /// <see cref="PinStyle"/> but with player-specific defaults; the player
    /// pin's outer Size is meaningful (drives Thumb bounds) while the survey
    /// pin's outer size still comes from <c>SurveyPinRadiusMetres</c>.</summary>
    public LegolasPinStyle PlayerPinStyle => _settings?.PlayerPinStyle ?? _defaultPlayerPinStyle;

    /// <summary>Active-pin highlight configuration. Falls back to defaults
    /// when the simpler test constructor is used (no settings).</summary>
    public LegolasActivePinStyle ActivePinStyle => _settings?.ActivePinStyle ?? _defaultActivePinStyle;

    /// <summary>In-flow (#460/#477A) calibration marker appearance (#478).
    /// <c>Outer</c> = the selection ring (drawn only while the marker is
    /// selected); <c>Center</c> = the always-on dot. Drives the overlay's
    /// calibration-marker DataTemplate; falls back to
    /// <see cref="LegolasPinStyle.CalibrationDefaults"/> for the test ctor.</summary>
    public LegolasPinStyle CalibrationPinStyle => _settings?.CalibrationPinStyle ?? _defaultCalibrationPinStyle;

    /// <summary>
    /// Context-aware on-overlay instruction. Empty during normal use; appears
    /// for the two states where a new user can stall:
    ///  * AwaitingPosition: needs a click to set the anchor.
    ///  * Listening with anchor placed but no surveys yet: the anchor's initial
    ///    projection scale can stick the pin off-screen (#131 follow-up). The
    ///    drag-anywhere gesture rescues it; the hint tells the user how.
    /// Hidden in Gathering/Done where the route geometry speaks for itself.
    /// </summary>
    // #454 retired the anchor-bootstrap states this hint coached through.
    // Absolute placement needs no setup. #476 reuses the (still-empty by
    // default) hint to coach the optional manual position-override click.
    public string OverlayHint =>
        IsSettingPosition
            ? "Click the map where your character is standing now."
            : string.Empty;

    public bool IsOverlayHintVisible => !string.IsNullOrEmpty(OverlayHint);

    public PixelPoint PlayerPosition
    {
        get => _session.PlayerPosition;
        set => _session.PlayerPosition = value;
    }

    /// <summary>
    /// The "you are here" pixel the overlay renderer should draw, or null for
    /// no marker. Mode-routed (#476): Motherlode keeps its manual-click anchor
    /// (only when one has been recorded); Survey uses the projected tracker
    /// GPS (null until a fix lands in a calibrated area — degrades silently,
    /// same as pre-#476). Never presented as live: pair it with
    /// <see cref="PlayerAnchorStatus"/> so the staleness is honest.
    /// </summary>
    public PixelPoint? PlayerMarkerPixel =>
        _session.Mode == SessionMode.Motherlode
            ? (_session.HasPlayerPosition ? _session.PlayerPosition : null)
            : _session.SurveyPlayerPixel;

    /// <summary>
    /// Short staleness label for the Survey player-GPS, e.g.
    /// <c>"You — zone-in, 4m ago"</c>, or <c>"You — set manually"</c> for the
    /// #476 Option&#160;C override. Empty outside Survey mode or when no
    /// anchor has resolved. The auto signal is sparse (zone-in / teleport
    /// only); this exists so the UI never implies the marker is live.
    /// </summary>
    public string PlayerAnchorStatus
    {
        get
        {
            if (_session.Mode != SessionMode.Survey || !_session.SurveyPlayerPixel.HasValue)
                return string.Empty;
            if (_session.SurveyPlayerIsManual)
                return "You — set manually";
            return _session.SurveyPlayerMeasuredAt is { } at && _session.SurveyPlayerSource is { } src
                ? FormatAnchorStatus(at, src, DateTimeOffset.UtcNow)
                : string.Empty;
        }
    }

    public bool IsPlayerAnchorStatusVisible => !string.IsNullOrEmpty(PlayerAnchorStatus);

    /// <summary>
    /// Pure staleness formatter (testable without a clock dependency). Source
    /// names the freshness class — <c>Spawn</c> is the zone-in/login anchor
    /// (freshest, the typical Optimize-time state), <c>Movement</c> a sparse
    /// teleport. The age is "as of <paramref name="now"/>"; it grows between
    /// the sparse fixes, which is the honest signal that the player has likely
    /// walked away from it.
    /// </summary>
    public static string FormatAnchorStatus(DateTimeOffset measuredAt, PlayerPositionSource source, DateTimeOffset now)
    {
        var kind = source == PlayerPositionSource.Spawn ? "zone-in" : "teleport";
        var age = now - measuredAt;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        string ago = age.TotalSeconds < 60
            ? "just now"
            : age.TotalMinutes < 60
                ? $"{(int)age.TotalMinutes}m ago"
                : $"{(int)age.TotalHours}h ago";
        return $"You — {kind}, {ago}";
    }

    public bool ShowBearingWedges
    {
        get => _session.ShowBearingWedges;
        set => _session.ShowBearingWedges = value;
    }

    [ObservableProperty]
    private IReadOnlyList<PixelPoint> _routePoints = Array.Empty<PixelPoint>();

    /// <summary>
    /// Two-point polyline drawn on top of the static route line: from the
    /// most-recently-collected pin — or, before the first collection, the
    /// player's projected GPS anchor (#476) — to the current
    /// <see cref="SurveyItemViewModel.IsActiveTarget"/> pin. The GPS is sparse
    /// (zone-in / teleport only), so once the player is walking the route the
    /// last-collected pin is the better proxy for "where they are now"; the
    /// anchor only seeds the very first segment. Empty when there's no active
    /// target, or before the first collection in an uncalibrated area.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<PixelPoint> _activeSegmentPoints = Array.Empty<PixelPoint>();

    /// <summary>
    /// Record the player's position from a map click. #454 retired this for
    /// Survey (absolute placement needs no anchor); it survives <b>for
    /// Motherlode</b>, whose triangulation reads
    /// <see cref="SessionState.PlayerPosition"/> via
    /// <c>MotherlodeViewModel.RecordPlayerPosition</c>. No Survey FSM
    /// involvement any more.
    /// </summary>
    [RelayCommand]
    public void SetPlayerPosition(PixelPoint where)
    {
        _session.PlayerPosition = where;
        _session.HasPlayerPosition = true;
        _projector.SetOrigin(where);
    }

    [RelayCommand]
    public void HandleMapClick(PixelPoint where)
    {
        // Motherlode: the click records the player position for triangulation.
        if (_session.Mode == SessionMode.Motherlode)
        {
            SetPlayerPosition(where);
            return;
        }

        // Survey: placement is automatic + absolute (ProcessMapFx), so a click
        // normally does nothing. The one exception is the #476 Option C
        // manual-override detour: while SettingPosition, the click is the
        // stale-anchor correction. Record it and return to the parked phase.
        if (IsSettingPosition)
        {
            RecordManualPosition(where);
            _surveyFlow.ConfirmPosition();
        }
    }

    public double Scale => _projector.Scale;
    public double RotationDegrees => _projector.RotationRadians * 180.0 / Math.PI;

    // The slider value is treated as pixel radius for predictable on-screen sizing —
    // multiplying by projector.Scale caused the pin to visibly shrink/grow after
    // every refit, which felt like a bug to the user.
    public double PinRadius => _settings?.SurveyPinRadiusMetres ?? 8.0;
    public double PinDiameter => PinRadius * 2;

    /// <summary>
    /// Drag/nudge a pin to a new pixel. #454: pins are absolute, so this is a
    /// purely local correction of where this one marker draws — it no longer
    /// drives a projector Refit (the relative-calibration model is retired).
    /// </summary>
    [RelayCommand]
    public void CorrectSurvey(CorrectionArgs args)
    {
        var vm = args.Survey;
        vm.UpdateModel(vm.Model with { ManualOverride = args.NewPixel });
        RebuildRouteGeometry();
    }

    [RelayCommand]
    private void OptimizeRoute()
    {
        var points = new List<PixelPoint>();
        var indices = new List<int>();
        for (var i = 0; i < Surveys.Count; i++)
        {
            var s = Surveys[i];
            if (s.Collected || s.Skipped) continue;
            if (!s.EffectivePixel.HasValue) continue;
            points.Add(s.EffectivePixel.Value);
            indices.Add(i);
        }
        if (points.Count == 0) return;

        // #476: start the tour from the player's projected GPS when one has
        // resolved (calibrated area + a tracker fix) — "nearest node to me
        // first", parity with Motherlode. Falls back to the first uncollected
        // pin when there's no anchor (uncalibrated area / no fix yet), which
        // is the #454 behaviour. `start` is a separate origin, not a member of
        // `points`, so `order`/`indices` are unaffected by the choice.
        var start = _session.SurveyPlayerPixel ?? points[0];
        var order = _optimizer.Optimize(start, points);
        for (var i = 0; i < Surveys.Count; i++)
        {
            Surveys[i].UpdateModel(Surveys[i].Model with { RouteOrder = null });
        }
        for (var i = 0; i < order.Count; i++)
        {
            var src = indices[order[i]];
            Surveys[src].UpdateModel(Surveys[src].Model with { RouteOrder = i });
        }
        RebuildRouteGeometry();
        _surveyFlow.OptimizeRoute();
    }

    private const double WedgeHalfAngleRadians = Math.PI / 8; // 22.5 degrees

    private void RebuildAllWedges()
    {
        foreach (var s in Surveys) RebuildWedgeFor(s);
    }

    private void RebuildWedgeFor(SurveyItemViewModel s)
    {
        // Wedges only render in Motherlode mode. Survey mode's 4-DOF refit
        // (PR #130) lands pins essentially pixel-perfect, so the bearing arc
        // adds no information — the optimised route + the placed pins are
        // sufficient and precise. Motherlode triangulation has no comparable
        // refit, so the bearing arc still narrows the search there.
        if (!_session.ShowBearingWedges
            || _session.Mode != SessionMode.Motherlode
            || s.IsCorrected
            || s.Collected
            || s.Skipped
            || s.Offset.Magnitude < 1e-6)
        {
            s.WedgeArc = null;
            return;
        }

        var distancePx = s.Offset.Magnitude * _projector.Scale;
        if (distancePx < 4)
        {
            s.WedgeArc = null;
            return;
        }

        var bearingOffset = Math.Atan2(s.Offset.East, s.Offset.North);
        var bearing = bearingOffset + _projector.RotationRadians;

        // Raw inputs only; the D2D renderer constructs the arc each frame.
        s.WedgeArc = new WedgeArc(
            Origin: PlayerPosition,
            BearingRadians: bearing,
            HalfAngleRadians: WedgeHalfAngleRadians,
            DistancePx: distancePx);
    }

    private void RebuildRouteGeometry()
    {
        if (!_session.ShowRouteLines)
        {
            RoutePoints = Array.Empty<PixelPoint>();
            ActiveSegmentPoints = Array.Empty<PixelPoint>();
            return;
        }

        var ordered = Surveys
            .Where(s => s.RouteOrder.HasValue && s.EffectivePixel.HasValue)
            .OrderBy(s => s.RouteOrder!.Value)
            .ToList();

        // #454: no player anchor — the route is just the ordered pins.
        var points = new List<PixelPoint>(ordered.Count);
        foreach (var s in ordered) points.Add(s.EffectivePixel!.Value);
        RoutePoints = points;

        RebuildActiveSegment();
    }

    private void RebuildActiveSegment()
    {
        if (!_session.ShowRouteLines)
        {
            ActiveSegmentPoints = Array.Empty<PixelPoint>();
            return;
        }

        var active = Surveys.FirstOrDefault(s => s.IsActiveTarget);
        if (active is null || !active.EffectivePixel.HasValue)
        {
            ActiveSegmentPoints = Array.Empty<PixelPoint>();
            return;
        }

        // The live segment runs from the most-recently-collected pin (best
        // available "where the player is now" proxy once they're walking the
        // route) to the active target. #476: before the first collection,
        // start from the player's projected GPS if one resolved — restores the
        // "from you → first node" guidance segment at run start. With neither
        // (uncalibrated area / no fix, nothing collected) there's no start
        // point, so just highlight the target (the #454 fallback).
        var lastCollected = Surveys
            .Where(s => s.Collected && s.RouteOrder.HasValue && s.EffectivePixel.HasValue)
            .OrderByDescending(s => s.RouteOrder!.Value)
            .FirstOrDefault();

        PixelPoint? start = lastCollected?.EffectivePixel ?? _session.SurveyPlayerPixel;
        ActiveSegmentPoints = start is { } s0
            ? new[] { s0, active.EffectivePixel.Value }
            : Array.Empty<PixelPoint>();
    }
}

public sealed record CorrectionArgs(SurveyItemViewModel Survey, PixelPoint NewPixel);
