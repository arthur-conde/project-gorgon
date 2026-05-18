using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Legolas.Domain;

namespace Legolas.Services;

/// <summary>
/// View-agnostic driver for #460 cold-start pin calibration done <b>through
/// the map overlay</b> (not the messy standalone calibration window — that
/// path is left untouched/redundant per the #460 decision). Reuses only the
/// clean service core: subscribes to <see cref="IAreaCalibrationService.PinAdded"/>
/// (fed by the Player.log <c>ProcessMapPinAdd</c> ingestion) and calls
/// <see cref="IAreaCalibrationService.CalibrateCurrentArea"/> /
/// <c>LandmarkCalibrationSolver</c>.
///
/// <para><b>Replay-arming.</b> <c>ProcessMapPinAdd</c> bulk-replays on area
/// entry, so world points are queued <em>only while armed</em> (the wizard
/// arms on entering the <c>Calibrating</c> step). The standalone window's
/// own pin-cal consumer gates on its own arm independently — coexistence is
/// safe.</para>
///
/// <para><b>Label-agnostic.</b> <see cref="IAreaCalibrationService.PinAdded"/>
/// carries a bare <see cref="WorldCoord"/> — there is structurally no pin
/// label here. Pairing is the user's overlay click in turn order: each click
/// pairs the <em>oldest</em> unpaired world point (hard rule #454).</para>
/// </summary>
public sealed partial class PinCalibrationCoordinator : ObservableObject
{
    private readonly IAreaCalibrationService _service;
    private readonly Queue<WorldCoord> _pending = new();
    private readonly List<(WorldCoord World, PixelPoint Pixel)> _pairs = new();

    public PinCalibrationCoordinator(IAreaCalibrationService service)
    {
        _service = service;
        _service.PinAdded += OnPinAdded;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isArmed;

    /// <summary>World points dropped (via <c>ProcessMapPinAdd</c>) and not yet
    /// paired with an overlay click.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Click-paired (world ↔ pixel) points accumulated so far.</summary>
    public int PairedCount => _pairs.Count;

    /// <summary>≥3 well-spread pairs are needed for a stable solve.</summary>
    public bool CanSolve => _pairs.Count >= 3;

    /// <summary>Markers the map overlay renders at each paired click pixel.</summary>
    public ObservableCollection<PixelPoint> PlacedMarkers { get; } = new();

    public string StatusText => IsArmed
        ? $"Drop ≥3 well-spread map pins in-game, then click each on the map " +
          $"in the same order. Paired: {PairedCount}  ·  awaiting click: {PendingCount}."
        : "Pin calibration is off.";

    /// <summary>Arm capture and clear any stale state so the area-entry
    /// replay backlog can't leak in.</summary>
    public void Arm()
    {
        _pending.Clear();
        _pairs.Clear();
        PlacedMarkers.Clear();
        IsArmed = true;
        RaiseCounts();
    }

    /// <summary>Disarm and flush — leaving the calibration step, or after a
    /// solve/clear.</summary>
    public void Disarm()
    {
        _pending.Clear();
        _pairs.Clear();
        PlacedMarkers.Clear();
        IsArmed = false;
        RaiseCounts();
    }

    /// <summary>
    /// Pair the next overlay click. Dequeues the oldest unpaired world point
    /// (turn order) and binds it to <paramref name="pixel"/>. No-op when
    /// disarmed or nothing is pending.
    /// </summary>
    public void PairClick(PixelPoint pixel)
    {
        if (!IsArmed || _pending.Count == 0) return;
        var world = _pending.Dequeue();
        _pairs.Add((world, pixel));
        PlacedMarkers.Add(pixel);
        RaiseCounts();
    }

    /// <summary>
    /// Solve + persist + apply the area calibration from the accumulated
    /// pairs (reuses the shipped solver verbatim). Returns the calibration,
    /// or null if &lt;3 pairs / the solve failed. On success the area becomes
    /// calibrated (<see cref="IAreaCalibrationService.Changed"/> fires) and
    /// the coordinator disarms.
    /// </summary>
    public AreaCalibration? Solve()
    {
        if (!CanSolve) return null;
        // Zoom OCR/CV is deferred (#460) — calibrate at the default zoom
        // stamp, consistent with the standalone window's unset-zoom path.
        var result = _service.CalibrateCurrentArea(_pairs.ToList(), calibrationZoom: 1.0);
        if (result is not null) Disarm();
        return result;
    }

    private void OnPinAdded(object? sender, WorldCoord world)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is not null && !disp.CheckAccess()) disp.Invoke(() => Enqueue(world));
        else Enqueue(world);
    }

    private void Enqueue(WorldCoord world)
    {
        if (!IsArmed) return; // disarmed → drop (this is how replay is discarded)
        _pending.Enqueue(world);
        RaiseCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PairedCount));
        OnPropertyChanged(nameof(CanSolve));
        OnPropertyChanged(nameof(StatusText));
    }
}
