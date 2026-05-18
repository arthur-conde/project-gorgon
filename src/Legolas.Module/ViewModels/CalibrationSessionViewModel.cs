using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Services;

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
        Refresh();
    }

    public ObservableCollection<CalibrationReference> References { get; } = new();
    public ObservableCollection<PlacedReference> Placements { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SolveCommand))]
    private CalibrationReference? _selectedReference;

    [ObservableProperty] private string _statusText = "No area detected yet.";
    [ObservableProperty] private string _instruction =
        "Walk into an area, pick a landmark or NPC below, then click exactly where it sits on the in-game map.";
    [ObservableProperty] private string? _resultText;

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

        if (_service.CurrentAreaFriendlyName is not { } area)
        {
            StatusText = "No area detected yet — walk into an area in-game.";
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
        if (SelectedReference is not { } reference) return;

        // Replace any prior placement of the same reference (a re-click is a
        // correction, not a second point).
        for (var i = Placements.Count - 1; i >= 0; i--)
            if (ReferenceEquals(Placements[i].Reference, reference))
                Placements.RemoveAt(i);

        Placements.Add(new PlacedReference(reference, pixel));
        OnPropertyChanged(nameof(CanSolve));
        SolveCommand.NotifyCanExecuteChanged();

        SelectedReference = References.FirstOrDefault(r =>
            Placements.All(p => !ReferenceEquals(p.Reference, r)));
    }

    [RelayCommand]
    private void RemoveLastPlacement()
    {
        if (Placements.Count == 0) return;
        Placements.RemoveAt(Placements.Count - 1);
        OnPropertyChanged(nameof(CanSolve));
        SolveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearPlacements()
    {
        Placements.Clear();
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
              "Surveys & treasure now project correctly here."
            : $"Solved but residual is high ({calibration.ResidualPixels:0.0} px) — references were likely " +
              "placed imprecisely or the map was at a different zoom. Clear and redo for a tighter fit.";
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

/// <summary>A reference the user has pinned on the map; <see cref="X"/>/<see cref="Y"/>
/// expose the click pixel for Canvas marker placement.</summary>
public sealed class PlacedReference
{
    public PlacedReference(CalibrationReference reference, PixelPoint pixel)
    {
        Reference = reference;
        Pixel = pixel;
    }

    public CalibrationReference Reference { get; }
    public PixelPoint Pixel { get; }
    public string Name => Reference.Name;
    public string Kind => Reference.Kind;
    public double X => Pixel.X;
    public double Y => Pixel.Y;
}
