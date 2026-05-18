using Legolas.Domain;
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
    /// Handle the chat banner. Resolves the friendly name to an internal area
    /// key; if a calibration is already persisted for it, applies it to the
    /// projector immediately so the first survey/treasure projects correctly.
    /// </summary>
    void OnAreaEntered(string areaFriendlyName);

    /// <summary>
    /// Manually set the current area by internal key (the area-picker path).
    /// Same effect as <see cref="OnAreaEntered"/> but bypasses friendly-name
    /// resolution — used when the chat banner was missed.
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

    /// <summary>
    /// Fed every <c>ProcessMapPinAdd</c> world coord by the Player.log
    /// pipeline (#454). Re-raised as <see cref="PinAdded"/> so the calibration
    /// window — and only when the user has armed pin calibration — can pair it
    /// with an overlay click. A no-op for everyone else; this decouples the
    /// ingestion service from the VM exactly like <see cref="NoteSurvey"/>.
    /// </summary>
    void NotePinAdded(WorldCoord world);

    /// <summary>Raised for each <see cref="NotePinAdded"/> — the pin-calibration hook.</summary>
    event EventHandler<WorldCoord>? PinAdded;
}

/// <summary>
/// Owns the per-area calibration lifecycle: chat-banner area detection &#8594;
/// internal-key resolution &#8594; apply persisted <see cref="AreaCalibration"/> on
/// entry, and the solve/persist path the calibration window drives. Reference
/// points come from <see cref="IReferenceDataService"/> (landmarks + NPCs with a
/// parseable <c>Pos</c>), which is the same engine-unit world frame the game
/// positions the player in (verified 2026-05-18).
/// </summary>
public sealed class AreaCalibrationService : IAreaCalibrationService
{
    private readonly IReferenceDataService _refData;
    private readonly LegolasSettings _settings;
    private readonly ICoordinateProjector _projector;
    private readonly SettingsAutoSaver<LegolasSettings> _saver;

    private IReadOnlyList<CalibrationReference> _currentRefs = Array.Empty<CalibrationReference>();

    public AreaCalibrationService(
        IReferenceDataService refData,
        LegolasSettings settings,
        ICoordinateProjector projector,
        SettingsAutoSaver<LegolasSettings> saver)
    {
        _refData = refData;
        _settings = settings;
        _projector = projector;
        _saver = saver;
    }

    public string? CurrentAreaKey { get; private set; }
    public string? CurrentAreaFriendlyName { get; private set; }

    public bool IsCurrentAreaCalibrated =>
        CurrentAreaKey is { } k && _settings.AreaCalibrations.ContainsKey(k);

    public AreaCalibration? CurrentCalibration =>
        CurrentAreaKey is { } k && _settings.AreaCalibrations.TryGetValue(k, out var c) ? c : null;

    public IReadOnlyList<CalibrationReference> CurrentAreaReferences => _currentRefs;

    private IReadOnlyList<AreaEntry>? _allAreas;
    public IReadOnlyList<AreaEntry> AllAreas =>
        _allAreas ??= _refData.Areas.Values
            .OrderBy(a => a.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public event EventHandler? Changed;

    public void OnAreaEntered(string areaFriendlyName)
    {
        if (string.IsNullOrWhiteSpace(areaFriendlyName)) return;
        // Even if the key can't be resolved (unknown / missing reference data),
        // record the raw friendly name so the UI can show it.
        SetArea(ResolveAreaKey(areaFriendlyName), areaFriendlyName.Trim());
    }

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

        if (key is not null
            && _settings.AreaCalibrations.TryGetValue(key, out var calibration))
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

        _settings.AreaCalibrations[key] = calibration;
        _saver.Touch(); // AreaCalibrations is a sibling object — no PropertyChanged.
        _projector.ApplyCalibration(calibration);
        Changed?.Invoke(this, EventArgs.Empty);
        return calibration;
    }

    public event EventHandler<CalibrationSurveyObservation>? SurveyObserved;

    public void NoteSurvey(string name, MetreOffset offset) =>
        SurveyObserved?.Invoke(this, new CalibrationSurveyObservation(name, offset));

    public event EventHandler<WorldCoord>? PinAdded;

    public void NotePinAdded(WorldCoord world) =>
        PinAdded?.Invoke(this, world);

    public void ClearCurrentAreaCalibration()
    {
        if (CurrentAreaKey is not { } key) return;
        if (_settings.AreaCalibrations.Remove(key))
        {
            _saver.Touch();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Friendly name &#8594; internal area key. Matches <see cref="AreaEntry.FriendlyName"/>
    /// first, then <see cref="AreaEntry.ShortFriendlyName"/>, case-insensitively.
    /// </summary>
    private string? ResolveAreaKey(string friendlyName)
    {
        var name = friendlyName.Trim();
        foreach (var area in _refData.Areas.Values)
        {
            if (string.Equals(area.FriendlyName, name, StringComparison.OrdinalIgnoreCase))
                return area.Key;
        }
        foreach (var area in _refData.Areas.Values)
        {
            if (!string.IsNullOrEmpty(area.ShortFriendlyName)
                && string.Equals(area.ShortFriendlyName, name, StringComparison.OrdinalIgnoreCase))
                return area.Key;
        }
        return null;
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
