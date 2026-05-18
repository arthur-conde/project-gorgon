using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Services;
using Mithril.Shared.Reference;

namespace Legolas.ViewModels;

/// <summary>
/// Drives the standalone calibration overlay: lists the current area's
/// landmark/NPC references, accumulates the user's reference clicks (a known
/// world point paired with where they clicked it on the in-game map, captured
/// through the transparent overlay), and solves+persists the per-area projector
/// calibration via <see cref="IAreaCalibrationService"/>.
///
/// Calibration is decoupled from the survey FSM entirely — placing references
/// here never touches <see cref="SessionState"/> or the survey pipeline; it only
/// feeds the solver. Once solved, every later survey/treasure in that area
/// projects correctly with no warmup.
/// </summary>
public sealed partial class CalibrationSessionViewModel : ObservableObject
{
    private readonly IAreaCalibrationService _service;

    public CalibrationSessionViewModel(IAreaCalibrationService service)
    {
        _service = service;
        _service.Changed += OnServiceChanged;
        _service.SurveyObserved += OnSurveyObserved;
        Refresh();
    }

    public ObservableCollection<CalibrationReference> References { get; } = new();
    public ObservableCollection<PlacedReference> Placements { get; } = new();

    /// <summary>Projected test pins (a survey/treasure fired while test mode is
    /// on, projected from <see cref="TestOrigin"/> via the live calibration).</summary>
    public ObservableCollection<TestPin> TestPins { get; } = new();

    /// <summary>Test mode: a click sets a synthetic "you are here" origin, and
    /// each subsequent survey/treasure drops a projected pin to eyeball against
    /// the real in-game ping — verifies the calibration without a survey run.</summary>
    [ObservableProperty] private bool _testMode;

    /// <summary>The synthetic player position test projections emanate from.</summary>
    [ObservableProperty] private PixelPoint? _testOrigin;

    public bool HasTestOrigin => TestOrigin is not null;
    public double TestOriginX => TestOrigin?.X ?? 0;
    public double TestOriginY => TestOrigin?.Y ?? 0;

    partial void OnTestOriginChanged(PixelPoint? value)
    {
        OnPropertyChanged(nameof(HasTestOrigin));
        OnPropertyChanged(nameof(TestOriginX));
        OnPropertyChanged(nameof(TestOriginY));
        OnPropertyChanged(nameof(NudgeTargetText));
        OnPropertyChanged(nameof(CanNudge));
        ReprojectTestPins(); // green pins follow your position as you nudge it
        RaiseDebug();
    }

    /// <summary>Every known area, for the manual picker (no live banner needed).</summary>
    public IReadOnlyList<AreaEntry> AvailableAreas => _service.AllAreas;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SolveCommand))]
    private CalibrationReference? _selectedReference;

    /// <summary>Manually-chosen area. Switching it drives the service the same
    /// way a live <c>Entering Area:</c> banner would.</summary>
    [ObservableProperty] private AreaEntry? _selectedArea;

    /// <summary>The placement the keyboard nudge acts on (set to the most
    /// recently placed; also click-selectable in the Placed list).</summary>
    [ObservableProperty] private PlacedReference? _selectedPlacement;

    /// <summary>Panel text naming what a nudge will move (null = nothing armed).
    /// In verify mode with a position set, the arrows move <em>your position</em>
    /// (and the green pins follow); otherwise the selected reference.</summary>
    public string? NudgeTargetText =>
        TestMode && TestOrigin is not null
            ? "Nudging your position — arrow keys move it, green pins follow (Shift = 5px)"
            : SelectedPlacement is { } p
                ? $"Nudging “{p.Name}” — arrow keys move it (Shift = 5px)"
                : null;

    /// <summary>True when arrow keys have something to move (a placement, or the
    /// test position in verify mode). Drives the code-behind key handler.</summary>
    public bool CanNudge => (TestMode && TestOrigin is not null) || SelectedPlacement is not null;

    /// <summary>Blunt always-visible state readout — so "no pin" is never a
    /// mystery: it shows whether the area/refs loaded, what's selected, how many
    /// pins exist + the last one's pixel, and the test origin. If a click made a
    /// pin, <c>pins=</c> increments here even if the on-map marker doesn't draw.</summary>
    public string DebugState =>
        $"area={_service.CurrentAreaKey ?? "-"}  refs={References.Count}  " +
        $"sel={SelectedReference?.Name ?? "-"}  pins={Placements.Count}  " +
        $"last={(Placements.Count > 0 ? $"({Placements[^1].X:0},{Placements[^1].Y:0})" : "-")}  " +
        $"you={(TestOrigin is { } o ? $"({o.X:0},{o.Y:0})" : "-")}  " +
        $"test={TestMode}  pinsShown={TestPins.Count}";

    private void RaiseDebug() => OnPropertyChanged(nameof(DebugState));

    partial void OnSelectedPlacementChanged(PlacedReference? oldValue, PlacedReference? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        // Selecting a pin in the Placed list also selects its reference (so the
        // References list agrees — "pick from either list"). Only sync upward
        // when a pin is chosen; clearing the pin because an *unplaced*
        // reference was picked must NOT wipe that reference selection.
        if (newValue is not null && !ReferenceEquals(SelectedReference, newValue.Reference))
            SelectedReference = newValue.Reference;
        OnPropertyChanged(nameof(NudgeTargetText));
        OnPropertyChanged(nameof(CanNudge));
        RaiseDebug();
    }

    /// <summary>
    /// Selecting a reference (either list) makes it the active target and links
    /// to its dropped pin if one exists — so nudging only works once a pin has
    /// been placed for it. Switching references leaves the previous pin where
    /// it was (it's already stored in <see cref="Placements"/>).
    /// </summary>
    partial void OnSelectedReferenceChanged(CalibrationReference? value)
    {
        var pin = value is null
            ? null
            : Placements.FirstOrDefault(p => ReferenceEquals(p.Reference, value));
        if (!ReferenceEquals(SelectedPlacement, pin))
            SelectedPlacement = pin; // null until this reference is dropped
        RaiseDebug();
    }

    /// <summary>Non-null when the last map click could not be placed — tells the
    /// user why instead of silently doing nothing.</summary>
    [ObservableProperty] private string? _clickWarning;

    /// <summary>Always set whenever a survey/treasure reaches the window — proves
    /// the chat→pipeline→window path is alive, and says what's missing if it
    /// didn't project. Never silently swallowed.</summary>
    [ObservableProperty] private string? _lastSurveyText;

    [ObservableProperty] private string _statusText = "No area detected yet.";
    [ObservableProperty] private string _instruction =
        "Pick an area (or walk into one), choose a landmark/NPC, then click exactly where it sits on the in-game map. Arrow keys nudge the last point.";
    [ObservableProperty] private string? _resultText;

    partial void OnSelectedAreaChanged(AreaEntry? value)
    {
        // Guard the Refresh→set→change loop: Refresh sets SelectedArea to the
        // already-current area, which must NOT re-drive the service.
        if (value is null || value.Key == _service.CurrentAreaKey) return;
        _service.SelectArea(value.Key);
    }

    /// <summary>Arrow-key fine-tune. In verify mode with a position set it moves
    /// <em>your position</em> (and re-projects the green pins); otherwise it
    /// moves the selected reference placement. No-op if neither applies.</summary>
    public void NudgeSelected(double dx, double dy)
    {
        if (TestMode && TestOrigin is { } o)
        {
            // Moves your position; OnTestOriginChanged re-projects the pins.
            TestOrigin = new PixelPoint(o.X + dx, o.Y + dy);
            return;
        }
        if (SelectedPlacement is { } p)
            p.Pixel = new PixelPoint(p.Pixel.X + dx, p.Pixel.Y + dy);
    }

    public bool CanSolve => Placements.Count >= 2;

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        // Service.Changed already fires on the UI dispatcher in the live path
        // (LogIngestionService posts area events to the dispatcher), but guard
        // anyway — bound-collection mutation off the UI thread is a documented
        // Mithril footgun.
        var disp = Application.Current?.Dispatcher;
        if (disp is not null && !disp.CheckAccess()) disp.Invoke(Refresh);
        else Refresh();
    }

    private void Refresh()
    {
        RefreshCore();
        RaiseDebug();
    }

    private void RefreshCore()
    {
        References.Clear();
        foreach (var r in _service.CurrentAreaReferences) References.Add(r);

        // Keep the area picker reflecting the live area without re-driving the
        // service (OnSelectedAreaChanged guards on key equality).
        if (_service.CurrentAreaKey is { } curKey)
            SelectedArea = AvailableAreas.FirstOrDefault(a => a.Key == curKey);

        if (_service.CurrentAreaFriendlyName is not { } area)
        {
            StatusText = "No area detected — pick one from the dropdown, or walk into one in-game.";
            return;
        }

        if (_service.CurrentAreaKey is null)
        {
            StatusText = $"{area} — unrecognised area (no reference data); calibration unavailable.";
            return;
        }

        if (_service.CurrentCalibration is { } c)
        {
            StatusText = $"{area} — calibrated " +
                $"({c.ReferenceCount} refs, residual {c.ResidualPixels:0.0} px). " +
                "Recalibrate if pins still land wrong.";
        }
        else
        {
            StatusText = $"{area} — not calibrated. Place ≥2 references and Solve.";
        }
    }

    /// <summary>
    /// Record where the user clicked the currently-selected reference on the
    /// map overlay. Auto-advances the selection to the next not-yet-placed
    /// reference so a calibration run is a steady pick-less click cadence.
    /// </summary>
    [RelayCommand]
    private void PlaceSelectedAt(PixelPoint pixel)
    {
        if (SelectedReference is not { } reference)
        {
            // Don't fail silently — the click WAS captured, it just had no
            // target. Tell the user which precondition is missing.
            ClickWarning = References.Count == 0
                ? "No area selected — choose an area above first."
                : "Pick a landmark/NPC from the list, then click where it is on the map.";
            RaiseDebug();
            return;
        }

        // Replace any prior placement of the same reference (a re-click is a
        // correction, not a second point).
        for (var i = Placements.Count - 1; i >= 0; i--)
            if (ReferenceEquals(Placements[i].Reference, reference))
                Placements.RemoveAt(i);

        var placed = new PlacedReference(reference, pixel);
        Placements.Add(placed);
        SelectedPlacement = placed;     // dropped → now nudgeable
        SelectedReference = reference;  // stays the active target (both lists agree)
        ClickWarning = null;
        OnPropertyChanged(nameof(CanSolve));
        SolveCommand.NotifyCanExecuteChanged();

        // Selection deliberately persists: the just-dropped pin is the clearly
        // indicated active target (halo + "Nudging …" text). Arrow keys nudge
        // it; clicking again repositions it; picking another reference swaps
        // (the prior pin stays put — already stored); "Done" deselects so a
        // stray click/nudge does nothing. (Earlier the persistence was a *bug*
        // only because it was invisible and unstoppable — now it's an explicit,
        // visible state with an exit.)
        RaiseDebug();
    }

    /// <summary>
    /// Single entry point for a click on the map surface. In test mode a click
    /// sets the synthetic player origin; otherwise it places the selected
    /// reference. Keeps the code-behind dumb (it doesn't know the mode).
    /// </summary>
    [RelayCommand]
    private void ViewportClicked(PixelPoint pixel)
    {
        if (TestMode)
        {
            TestOrigin = pixel;
            TestPins.Clear();
            ClickWarning = _service.CurrentCalibration is null
                ? "Solve a calibration first, then click where you are to verify."
                : null;
            return;
        }
        PlaceSelectedAt(pixel);
    }

    /// <summary>Enter/leave the position+verify step (also auto-entered after a
    /// successful Solve — prompting for position is part of calibrating).</summary>
    [RelayCommand]
    private void ToggleTestMode()
    {
        TestMode = !TestMode;
        ClickWarning = null;
    }

    partial void OnTestModeChanged(bool value)
    {
        if (value)
        {
            TestPins.Clear();
            TestOrigin = null;
            Instruction = _service.CurrentCalibration is null
                ? "Place ≥2 references and Solve first — then click where you are and fire a survey/treasure to verify."
                : "Verify: click where you ARE on the map now, then use a survey/treasure — the projected pin should land on the real ping. (Surveying still asks for your position separately; this only checks the calibration.)";
        }
        else
        {
            Instruction = "Pick an area (or walk into one), choose a landmark/NPC, then click exactly where it sits on the in-game map. Arrow keys nudge the last point.";
        }
        OnPropertyChanged(nameof(NudgeTargetText));
        OnPropertyChanged(nameof(CanNudge));
        RaiseDebug();
    }

    /// <summary>Commit &amp; stop: deselect the active reference/pin so a stray
    /// click or arrow key does nothing until something is picked again. The
    /// pin's final nudged position is already stored in <see cref="Placements"/>.</summary>
    [RelayCommand]
    private void Deselect()
    {
        SelectedReference = null; // OnSelectedReferenceChanged clears the pin too
        SelectedPlacement = null;
        ClickWarning = null;
        RaiseDebug();
    }

    [RelayCommand]
    private void ClearTestPins()
    {
        TestPins.Clear();
        TestOrigin = null;
        RaiseDebug();
    }

    private void OnSurveyObserved(object? sender, CalibrationSurveyObservation obs)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is not null && !disp.CheckAccess()) disp.Invoke(() => { ProjectTest(obs); RaiseDebug(); });
        else { ProjectTest(obs); RaiseDebug(); }
    }

    private void ProjectTest(CalibrationSurveyObservation obs)
    {
        // ALWAYS record that the survey reached the window — before any gate —
        // so "nothing happened" is never ambiguous. This is the signal that the
        // chat→pipeline→window path is alive.
        var seen = $"Last survey: {obs.Name} ({obs.Offset.East:0}E, {obs.Offset.North:0}N)";

        if (!TestMode)
        {
            LastSurveyText = seen + " — turn on “Verify (set position)” to project it.";
            return;
        }
        if (_service.CurrentCalibration is not { } c)
        {
            LastSurveyText = seen + " — solve a calibration first.";
            return;
        }
        if (TestOrigin is not { } o)
        {
            LastSurveyText = seen + " — click where you are on the map first.";
            return;
        }

        TestPins.Add(new TestPin(obs.Name, obs.Offset, Project(c, o, obs.Offset)));
        LastSurveyText = seen + " → green pin projected.";
        ClickWarning = null;
    }

    /// <summary>Same math as <c>CoordinateProjector.Project</c>: a metre offset
    /// from <paramref name="origin"/> through the calibration's scale/rotation.</summary>
    private static PixelPoint Project(AreaCalibration c, PixelPoint origin, MetreOffset off)
    {
        var cos = Math.Cos(c.RotationRadians);
        var sin = Math.Sin(c.RotationRadians);
        var rotE = off.East * cos + off.North * sin;
        var rotN = -off.East * sin + off.North * cos;
        return new PixelPoint(origin.X + c.Scale * rotE, origin.Y - c.Scale * rotN);
    }

    /// <summary>Re-place every projected pin from the (moved) test origin so the
    /// green pins track your position live while you align them.</summary>
    private void ReprojectTestPins()
    {
        if (_service.CurrentCalibration is not { } c || TestOrigin is not { } o) return;
        foreach (var pin in TestPins)
            pin.Pixel = Project(c, o, pin.Offset);
    }

    [RelayCommand]
    private void RemoveLastPlacement()
    {
        if (Placements.Count == 0) return;
        var removed = Placements[^1];
        Placements.RemoveAt(Placements.Count - 1);
        if (ReferenceEquals(SelectedPlacement, removed)) SelectedPlacement = null;
        OnPropertyChanged(nameof(CanSolve));
        SolveCommand.NotifyCanExecuteChanged();
        RaiseDebug();
    }

    [RelayCommand]
    private void ClearPlacements()
    {
        Placements.Clear();
        SelectedPlacement = null; // don't leave the nudge target dangling
        OnPropertyChanged(nameof(CanSolve));
        SolveCommand.NotifyCanExecuteChanged();
        ResultText = null;
        RaiseDebug();
    }

    [RelayCommand(CanExecute = nameof(CanSolve))]
    private void Solve()
    {
        var pairs = Placements.Select(p => (p.Reference.World, p.Pixel)).ToList();
        var calibration = _service.CalibrateCurrentArea(pairs);
        if (calibration is null)
        {
            ResultText = "Couldn't solve — need ≥2 references at meaningfully different spots.";
            return;
        }

        ResultText = calibration.ResidualPixels <= 12
            ? $"Calibrated: {calibration.ResidualPixels:0.0} px residual, scale {calibration.Scale:0.000} px/m. " +
              "Now verify: click where you are, then use a survey/treasure."
            : $"Solved but residual is high ({calibration.ResidualPixels:0.0} px) — references were likely " +
              "placed imprecisely or the map was at a different zoom. Clear and redo for a tighter fit.";

        // Calibration isn't "done" until it's verified. Auto-continue into the
        // position+verify step so the user doesn't have to discover a separate
        // toggle — prompting for position is part of calibrating.
        TestMode = true;
        Refresh();
    }

    /// <summary>Drop the persisted calibration for this area and start over.</summary>
    [RelayCommand]
    private void Recalibrate()
    {
        _service.ClearCurrentAreaCalibration();
        ClearPlacements();
        Refresh();
    }
}

/// <summary>A reference the user has pinned on the map. Observable so an
/// arrow-key nudge to <see cref="Pixel"/> moves the on-map marker and updates
/// the Placed list live; <see cref="X"/>/<see cref="Y"/> drive Canvas placement.</summary>
/// <summary>A projected test reading. Keeps its source <see cref="Offset"/> so
/// it can be re-projected when the test origin (your position) is nudged —
/// the green pins track your position live while you align them.</summary>
public sealed partial class TestPin : ObservableObject
{
    public TestPin(string name, MetreOffset offset, PixelPoint pixel)
    {
        Name = name;
        Offset = offset;
        _pixel = pixel;
    }

    public string Name { get; }
    public MetreOffset Offset { get; }

    [ObservableProperty] private PixelPoint _pixel;

    partial void OnPixelChanged(PixelPoint value)
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
    }

    public double X => Pixel.X;
    public double Y => Pixel.Y;
}

public sealed partial class PlacedReference : ObservableObject
{
    public PlacedReference(CalibrationReference reference, PixelPoint pixel)
    {
        Reference = reference;
        _pixel = pixel;
    }

    public CalibrationReference Reference { get; }

    [ObservableProperty] private PixelPoint _pixel;

    /// <summary>True for the one placement the arrow-key nudge currently acts
    /// on — drives the on-map "active target" highlight so it's obvious what
    /// you're moving.</summary>
    [ObservableProperty] private bool _isSelected;

    partial void OnPixelChanged(PixelPoint value)
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
    }

    public string Name => Reference.Name;
    public string Kind => Reference.Kind;
    public double X => Pixel.X;
    public double Y => Pixel.Y;
}
