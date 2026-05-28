using Legolas.Domain;
using Mithril.MapCalibration;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;

namespace Legolas.Services;

public interface IAreaCalibrationService
{
    /// <summary>Internal area key of the area the player is currently in (e.g. <c>"AreaEltibule"</c>), or null if unknown.</summary>
    string? CurrentAreaKey { get; }

    /// <summary>Friendly name of the current area (as seen in the chat banner), or null.</summary>
    string? CurrentAreaFriendlyName { get; }

    /// <summary>True when the current area has a persisted <see cref="AreaCalibration"/> applied.</summary>
    bool IsCurrentAreaCalibrated { get; }

    /// <summary>Persisted calibration for the current area, if any.</summary>
    AreaCalibration? CurrentCalibration { get; }

    /// <summary>
    /// Landmark + NPC reference points in the current area, with parseable
    /// positions, ordered NPCs-then-landmarks (NPCs are the dense recognizable
    /// set). Empty when no current area or no reference data loaded.
    /// </summary>
    IReadOnlyList<CalibrationReference> CurrentAreaReferences { get; }

    /// <summary>
    /// Every known area (from reference data), sorted by friendly name — the
    /// source for the manual area picker. Lets the user calibrate without
    /// waiting for a live <c>Entering Area:</c> banner (e.g. Mithril was started
    /// after they were already in the area).
    /// </summary>
    IReadOnlyList<AreaEntry> AllAreas { get; }

    /// <summary>Raised (CurrentAreaKey changed or calibration (re)applied) so UI can refresh.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Set the current area by internal key. The Arda-driven
    /// <c>PlayerLogIngestionService</c> calls this when it receives an
    /// <c>AreaChanged</c> domain event (#605 — the prior chat
    /// <c>Entering Area:</c> banner path is gone; Arda's <c>IAreaState</c> is
    /// the authoritative source). Also used by the manual area-picker UI.
    /// </summary>
    void SelectArea(string areaKey);

    /// <summary>
    /// Solve a calibration from user-placed reference clicks (a world point
    /// paired with the pixel the user clicked it at) for the current area,
    /// persist it, and apply it to the projector. Returns the solved
    /// calibration, or null if it couldn't be solved (no current area, &lt;2
    /// non-degenerate references).
    /// </summary>
    AreaCalibration? CalibrateCurrentArea(
        IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements,
        double calibrationZoom = 1.0);

    /// <summary>Drop the current area's persisted calibration (forces a recalibrate).</summary>
    void ClearCurrentAreaCalibration();

    /// <summary>
    /// Fed every survey/treasure reading by the log pipeline. Re-raised as
    /// <see cref="SurveyObserved"/> so the calibration window's test mode can
    /// project it and show projected-vs-actual. A no-op for everyone else.
    /// </summary>
    void NoteSurvey(string name, MetreOffset offset);

    /// <summary>Raised for each <see cref="NoteSurvey"/> — the test-mode hook.</summary>
    event EventHandler<CalibrationSurveyObservation>? SurveyObserved;

    // Pin ingestion is no longer Legolas-owned: the map-pin lifecycle is
    // handled by the Arda pipeline (MapPinAdded/MapPinRemoved domain events),
    // which calibration consumers subscribe to directly. The old
    // NotePinAdded/PinAdded relay (#454) was removed with that promotion.
}

/// <summary>
/// Owns the per-area calibration lifecycle: area-key handoff (from the
/// Arda <c>AreaChanged</c> domain event bridge in
/// <c>PlayerLogIngestionService</c> or the manual area-picker UI) &#8594;
/// apply persisted <see cref="AreaCalibration"/> on entry, and the
/// solve/persist path the calibration window drives. Reference points
/// come from <see cref="IReferenceDataService"/> (landmarks + NPCs with a
/// parseable <c>Pos</c>), which is the same engine-unit world frame the game
/// positions the player in (verified 2026-05-18).
///
/// <para>The chat-log <c>Entering Area:</c> banner path was retired in #605 —
/// per #531, Arda's <c>IAreaState</c> already exposes the same signal
/// authoritatively from Player.log's <c>LOADING LEVEL</c> line.</para>
/// </summary>
public sealed class AreaCalibrationService : IAreaCalibrationService
{
    private readonly IReferenceDataService _refData;
    private readonly LegolasSettings _settings;
    private readonly ICoordinateProjector _projector;
    private readonly SettingsAutoSaver<LegolasSettings> _saver;
    private readonly IMapCalibrationService _mapCal;

    private IReadOnlyList<CalibrationReference> _currentRefs = Array.Empty<CalibrationReference>();

    public AreaCalibrationService(
        IReferenceDataService refData,
        LegolasSettings settings,
        ICoordinateProjector projector,
        SettingsAutoSaver<LegolasSettings> saver,
        IMapCalibrationService mapCal)
    {
        _refData = refData;
        _settings = settings;
        _projector = projector;
        _saver = saver;
        _mapCal = mapCal;

        // Re-apply the projector when the active calibration changes from a
        // source we don't own (e.g. a community-sync update lands for the
        // current area). Stacked-source precedence is honoured by GetCalibration.
        _mapCal.Changed += OnMapCalChanged;
    }

    public string? CurrentAreaKey { get; private set; }
    public string? CurrentAreaFriendlyName { get; private set; }

    public bool IsCurrentAreaCalibrated =>
        CurrentAreaKey is { } k && _mapCal.IsCalibrated(k);

    public AreaCalibration? CurrentCalibration =>
        CurrentAreaKey is { } k ? _mapCal.GetCalibration(k) : null;

    public IReadOnlyList<CalibrationReference> CurrentAreaReferences => _currentRefs;

    private IReadOnlyList<AreaEntry>? _allAreas;
    public IReadOnlyList<AreaEntry> AllAreas =>
        _allAreas ??= _refData.Areas.Values
            .OrderBy(a => a.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public event EventHandler? Changed;

    public void SelectArea(string areaKey)
    {
        if (string.IsNullOrWhiteSpace(areaKey)) return;
        if (_refData.Areas.TryGetValue(areaKey, out var entry))
            SetArea(entry.Key, entry.FriendlyName);
        else
            SetArea(areaKey, areaKey); // unknown key: still switch, no refs
    }

    private void SetArea(string? key, string friendlyName)
    {
        CurrentAreaFriendlyName = friendlyName;
        CurrentAreaKey = key;
        _currentRefs = key is null ? Array.Empty<CalibrationReference>() : BuildReferences(key);

        if (key is not null && _mapCal.GetCalibration(key) is { } calibration)
        {
            _projector.ApplyCalibration(calibration);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnMapCalChanged(object? sender, string areaKey)
    {
        if (!string.Equals(areaKey, CurrentAreaKey, StringComparison.Ordinal)) return;
        if (_mapCal.GetCalibration(areaKey) is { } calibration)
        {
            _projector.ApplyCalibration(calibration);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public AreaCalibration? CalibrateCurrentArea(
        IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements,
        double calibrationZoom = 1.0)
    {
        if (CurrentAreaKey is not { } key || placements is null || placements.Count < 2)
            return null;

        var refs = placements
            .Select(p => new LandmarkCalibrationSolver.Reference(p.World.X, p.World.Z, p.Pixel))
            .ToList();

        var solved = LandmarkCalibrationSolver.Solve(refs);
        if (solved is null) return null;
        // Stamp the zoom the user solved at (solver is zoom-agnostic — it just
        // fits the clicked pixels). > 0 guard so a bad value can't poison the
        // later currentZoom/CalibrationZoom division.
        var calibration = solved with
        {
            CalibrationZoom = calibrationZoom > 1e-6 ? calibrationZoom : 1.0,
        };

        // Dual-write for #836's transition window: writes go to BOTH the shared
        // service (canonical going forward) and LegolasSettings.AreaCalibrations
        // (legacy, removed in a follow-up release once we've shipped one cycle
        // of parity). A rollback to an older Mithril during the transition
        // therefore preserves the user's calibration in the place that older
        // Mithril expects to find it.
        _settings.AreaCalibrations[key] = calibration;
        _saver.Touch(); // AreaCalibrations is a sibling object — no PropertyChanged.
        // SaveUserRefinement raises IMapCalibrationService.Changed, which our
        // OnMapCalChanged handler re-broadcasts as our own Changed for the
        // current area. Do NOT also fire Changed directly here — that would
        // double-deliver (regression observed when both legacy + new paths
        // raised). Apply the projector here for the synchronous-after-solve
        // contract; the handler's projector-apply guards against the redundant
        // re-application by comparing area keys.
        _projector.ApplyCalibration(calibration);
        _mapCal.SaveUserRefinement(key, calibration);
        return calibration;
    }

    public event EventHandler<CalibrationSurveyObservation>? SurveyObserved;

    public void NoteSurvey(string name, MetreOffset offset) =>
        SurveyObserved?.Invoke(this, new CalibrationSurveyObservation(name, offset));

    public void ClearCurrentAreaCalibration()
    {
        if (CurrentAreaKey is not { } key) return;
        // Dual-clear, same rationale as the dual-write in CalibrateCurrentArea.
        // ClearUserRefinement raises mapCal.Changed → OnMapCalChanged re-broadcasts
        // our Changed; do not raise Changed directly to avoid double-delivery.
        if (_settings.AreaCalibrations.Remove(key)) _saver.Touch();
        _mapCal.ClearUserRefinement(key);
    }

    private IReadOnlyList<CalibrationReference> BuildReferences(string areaKey)
    {
        var list = new List<CalibrationReference>();

        // NPCs first — dense, named, labelled on the in-game map.
        foreach (var npc in _refData.NpcsByInternalName.Values)
        {
            if (!string.Equals(npc.AreaName, areaKey, StringComparison.Ordinal)) continue;
            if (WorldCoord.TryParse(npc.Pos) is not { } w) continue;
            list.Add(new CalibrationReference(npc.Name ?? "(unnamed NPC)", "NPC", w));
        }

        // Landmarks — sparse same-format supplement.
        if (_refData.Landmarks.TryGetValue(areaKey, out var landmarks))
        {
            foreach (var lm in landmarks)
            {
                if (WorldCoord.TryParse(lm.Loc) is not { } w) continue;
                var kind = string.IsNullOrEmpty(lm.Type) ? "Landmark" : lm.Type!;
                list.Add(new CalibrationReference(lm.Name ?? "(unnamed landmark)", kind, w));
            }
        }

        return list;
    }
}
