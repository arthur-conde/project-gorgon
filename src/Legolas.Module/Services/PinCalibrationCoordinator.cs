using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Legolas.Domain;
using Mithril.GameState.Pins;

namespace Legolas.Services;

/// <summary>
/// View-agnostic driver for cold-start pin calibration done <b>through the
/// map overlay</b>. Consumes the GameState-tier
/// <see cref="IPlayerPinTracker"/> (#468) — the authoritative, area-scoped,
/// replay-deduped player pin set — and feeds click-paired
/// <c>(WorldCoord ↔ PixelPoint)</c> placements to
/// <see cref="IAreaCalibrationService.CalibrateCurrentArea"/>.
///
/// <para><b>Two routes (both end in the same solve).</b></para>
/// <list type="number">
///   <item><b>Existing-pins route.</b> When the player already has ≥3
///   well-spread pins in this area, they're listed by their in-game identity
///   (<see cref="MapPin.Label"/> + <see cref="MapPin.Appearance"/>, e.g.
///   "Fire Magic 25 (red dot)"). The user selects one and clicks where it is
///   on the overlay — no re-dropping. The pairing is the user's
///   <em>deliberate</em> select-then-click; name/colour/shape are
///   <b>UX-only</b> and never reach the solver.</item>
///   <item><b>Freshly-dropped turn-order route.</b> The true cold-start case
///   (fogged brand-new area, no usable pins). Pins dropped <em>after</em>
///   arming queue up; each overlay click pairs the oldest unpaired one —
///   label-agnostic, by interaction order (hard rule #454).</item>
/// </list>
///
/// <para><b>Reconciliation of the #454 label-agnostic rule.</b> The solve is
/// purely <c>(WorldCoord ↔ PixelPoint)</c> in both routes — labels/colours
/// never feed <c>LandmarkCalibrationSolver</c>. The existing-pins route only
/// uses identity to help the <em>human</em> pick which service-supplied world
/// point they are clicking; the pairing is still a deliberate user click, so
/// the rule's intent (no auto-pairing by name) is preserved. See
/// docs/legolas-overview.md.</para>
///
/// <para><b>Replay is the service's job now.</b> <see cref="IPlayerPinTracker"/>
/// owns the login/area-entry bulk-replay (idempotent upsert keyed by
/// coordinate), so a backlog never reaches the turn-order queue: only genuine
/// post-arm <see cref="PinSetChange.Added"/> notifications enqueue. The
/// <see cref="IsArmed"/> gate additionally scopes the queue to "since the user
/// started calibrating".</para>
/// </summary>
public sealed partial class PinCalibrationCoordinator : ObservableObject
{
    private readonly IAreaCalibrationService _service;
    private readonly IPlayerPinTracker _pins;
    private readonly IDisposable _sub;

    // Turn-order route: pins dropped after arming, not yet click-paired.
    private readonly Queue<WorldCoord> _pending = new();
    // Accumulated solve pairs (both routes feed this).
    private readonly List<(WorldCoord World, PixelPoint Pixel)> _pairs = new();

    public PinCalibrationCoordinator(IAreaCalibrationService service, IPlayerPinTracker pins)
    {
        _service = service;
        _pins = pins;
        // Subscribe replays a Snapshot synchronously (seeds ExistingPins);
        // live notifications arrive on the tracker's ingestion thread.
        _sub = _pins.Subscribe(OnPinSetChanged);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isArmed;

    /// <summary>The pin the user has selected to click next (existing-pins
    /// route). Null falls back to the turn-order route.</summary>
    [ObservableProperty]
    private MapPin? _selectedExistingPin;

    /// <summary>Current-area pins available to calibrate against, by their
    /// in-game identity. Kept in sync with the GameState set.</summary>
    public ObservableCollection<MapPin> ExistingPins { get; } = new();

    /// <summary>≥3 well-spread existing pins ⇒ offer the friendlier route.</summary>
    public bool HasUsableExistingPins => ExistingPins.Count >= 3;

    /// <summary>Pins dropped post-arm and not yet paired (turn-order route).</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Click-paired (world ↔ pixel) points accumulated so far.</summary>
    public int PairedCount => _pairs.Count;

    /// <summary>≥3 well-spread pairs are needed for a stable solve.</summary>
    public bool CanSolve => _pairs.Count >= 3;

    /// <summary>Markers the map overlay renders at each paired click pixel.</summary>
    public ObservableCollection<PixelPoint> PlacedMarkers { get; } = new();

    public string StatusText
    {
        get
        {
            if (!IsArmed) return "Pin calibration is off.";
            var tail = $"Paired: {PairedCount}.";
            return HasUsableExistingPins
                ? $"Pick a pin below, then click where it is on the map. " +
                  $"You have {ExistingPins.Count} pins here. {tail}"
                : $"Drop ≥3 well-spread map pins in-game, then click each on " +
                  $"the map in the same order. Awaiting click: {PendingCount}. {tail}";
        }
    }

    /// <summary>Arm capture and clear any stale state. Seeds
    /// <see cref="ExistingPins"/> from the current GameState set so the
    /// existing-pins route is immediately usable.</summary>
    public void Arm()
    {
        _pending.Clear();
        _pairs.Clear();
        PlacedMarkers.Clear();
        SelectedExistingPin = null;
        SyncExistingPins(_pins.CurrentAreaPins);
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
        SelectedExistingPin = null;
        IsArmed = false;
        RaiseCounts();
    }

    /// <summary>
    /// Pair an overlay click. Existing-pins route when a pin is selected
    /// (pairs that pin's service-supplied world coord); otherwise the
    /// turn-order route (oldest unpaired post-arm pin). No-op when disarmed,
    /// nothing is selected, and nothing is pending. Degenerate duplicate
    /// world points are rejected so the solve stays well-conditioned.
    /// </summary>
    public void PairClick(PixelPoint pixel)
    {
        if (!IsArmed) return;

        WorldCoord world;
        if (SelectedExistingPin is { } sel)
        {
            world = new WorldCoord(sel.X, 0, sel.Z);
            SelectedExistingPin = null;
        }
        else if (_pending.Count > 0)
        {
            world = _pending.Dequeue();
        }
        else
        {
            return;
        }

        // Reject a second click on the same world point (both routes share
        // _pairs) — duplicates degrade the least-squares solve.
        if (_pairs.Any(p => Same(p.World, world))) { RaiseCounts(); return; }

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

    private void OnPinSetChanged(PinSetChanged note)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is not null && !disp.CheckAccess())
            disp.Invoke(() => Apply(note));
        else
            Apply(note);
    }

    private void Apply(PinSetChanged note)
    {
        // Keep the existing-pins picker mirroring the live set in all cases.
        SyncExistingPins(note.Pins);

        // Turn-order route: only genuinely-new pins dropped while armed
        // enqueue. The service already suppresses replay re-adds, so no
        // backlog can leak; the IsArmed gate scopes to the active session.
        if (note is { Kind: PinSetChange.Added, Pin: { } pin } && IsArmed)
        {
            _pending.Enqueue(new WorldCoord(pin.X, 0, pin.Z));
            RaiseCounts();
        }
    }

    /// <summary>Rebuild <see cref="ExistingPins"/> from a snapshot, preserving
    /// the current selection by value when the same pin survives.</summary>
    private void SyncExistingPins(IReadOnlyList<MapPin> pins)
    {
        var prev = SelectedExistingPin;
        ExistingPins.Clear();
        foreach (var p in pins) ExistingPins.Add(p);
        SelectedExistingPin = prev is null
            ? null
            : ExistingPins.FirstOrDefault(p => p == prev);
        OnPropertyChanged(nameof(HasUsableExistingPins));
        OnPropertyChanged(nameof(StatusText));
    }

    private static bool Same(WorldCoord a, WorldCoord b) =>
        Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Z - b.Z) < 0.01;

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PairedCount));
        OnPropertyChanged(nameof(CanSolve));
        OnPropertyChanged(nameof(StatusText));
    }
}
