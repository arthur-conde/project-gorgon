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

    /// <summary>Panel text naming what a nudge will move (null = nothing armed).</summary>
    public string? NudgeTargetText => SelectedPlacement is { } p
        ? $"Nudging “{p.Name}” — arrow keys move it (Shift = 5px)"
        : null;

    partial void OnSelectedPlacementChanged(PlacedReference? oldValue, PlacedReference? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        OnPropertyChanged(nameof(NudgeTargetText));
    }

    /// <summary>Non-null when the last map click could not be placed — tells the
    /// user why instead of silently doing nothing.</summary>
    [ObservableProperty] private string? _clickWarning;

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

    /// <summary>Move the selected placement by (dx, dy) screen pixels — the
    /// arrow-key fine-tune. No-op if nothing is selected.</summary>
    public void NudgeSelected(double dx, double dy)
    {
        if (SelectedPlacement is not { } p) return;
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
            return;
        }

        // Replace any prior placement of the same reference (a re-click is a
        // correction, not a second point).
        for (var i = Placements.Count - 1; i >= 0; i--)
            if (ReferenceEquals(Placements[i].Reference, reference))
                Placements.RemoveAt(i);

        var placed = new PlacedReference(reference, pixel);
        Placements.Add(placed);
        SelectedPlacement = placed; // immediately arrow-key nudgeable
        ClickWarning = null;
        OnPropertyChanged(nameof(CanSolve));
        SolveCommand.NotifyCanExecuteChanged();

        // Clear the selection so the click is "spent". Otherwise the reference
        // stays selected and the *next* click re-places (visibly moves) the
        // same pin — every stray click drags it. To correct a point: nudge it
        // with the arrow keys, or deliberately re-select that reference and
        // click again. A click never silently moves an existing pin.
        SelectedReference = null;
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
    }

    [RelayCommand]
    private void ClearTestPins()
    {
        TestPins.Clear();
        TestOrigin = null;
    }

    private void OnSurveyObserved(object? sender, CalibrationSurveyObservation obs)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is not null && !disp.CheckAccess()) disp.Invoke(() => ProjectTest(obs));
        else ProjectTest(obs);
    }

    private void ProjectTest(CalibrationSurveyObservation obs)
    {
        if (!TestMode) return;
        if (_service.CurrentCalibration is not { } c)
        {
            ClickWarning = "Solve a calibration before verifying.";
            return;
        }
        if (TestOrigin is not { } o)
        {
            ClickWarning = "Click where you are on the map first.";
            return;
        }

        // Same math as CoordinateProjector.Project, with origin = the test
        // position and scale/rotation from the live calibration.
        var cos = Math.Cos(c.RotationRadians);
        var sin = Math.Sin(c.RotationRadians);
        var rotE = obs.Offset.East * cos + obs.Offset.North * sin;
        var rotN = -obs.Offset.East * sin + obs.Offset.North * cos;
        var pixel = new PixelPoint(o.X + c.Scale * rotE, o.Y - c.Scale * rotN);
        TestPins.Add(new TestPin(obs.Name, pixel));
        ClickWarning = null;
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
    }

    [RelayCommand]
    private void ClearPlacements()
    {
        Placements.Clear();
        SelectedPlacement = null; // don't leave the nudge target dangling
        OnPropertyChanged(nameof(CanSolve));
        SolveCommand.NotifyCanExecuteChanged();
        ResultText = null;
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
/// <summary>A projected test reading dropped while test mode is active.
/// Static once placed (unlike <see cref="PlacedReference"/> it isn't nudged).</summary>
public sealed class TestPin
{
    public TestPin(string name, PixelPoint pixel)
    {
        Name = name;
        Pixel = pixel;
    }

    public string Name { get; }
    public PixelPoint Pixel { get; }
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
