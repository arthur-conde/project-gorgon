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

    /// <summary>Raised (CurrentAreaKey changed or calibration (re)applied) so UI can refresh.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Handle the chat banner. Resolves the friendly name to an internal area
    /// key; if a calibration is already persisted for it, applies it to the
    /// projector immediately so the first survey/treasure projects correctly.
    /// </summary>
    void OnAreaEntered(string areaFriendlyName);

    /// <summary>
    /// Solve a calibration from user-placed reference clicks (a world point
    /// paired with the pixel the user clicked it at) for the current area,
    /// persist it, and apply it to the projector. Returns the solved
    /// calibration, or null if it couldn't be solved (no current area, &lt;2
    /// non-degenerate references).
    /// </summary>
    AreaCalibration? CalibrateCurrentArea(IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements);

    /// <summary>Drop the current area's persisted calibration (forces a recalibrate).</summary>
    void ClearCurrentAreaCalibration();
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

    public event EventHandler? Changed;

    public void OnAreaEntered(string areaFriendlyName)
    {
        if (string.IsNullOrWhiteSpace(areaFriendlyName)) return;

        var key = ResolveAreaKey(areaFriendlyName);
        // Even if we can't resolve the key (reference data missing / unknown
        // friendly name), record what we saw so the UI can show the raw name.
        CurrentAreaFriendlyName = areaFriendlyName.Trim();
        CurrentAreaKey = key;
        _currentRefs = key is null ? Array.Empty<CalibrationReference>() : BuildReferences(key);

        if (key is not null
            && _settings.AreaCalibrations.TryGetValue(key, out var calibration))
        {
            _projector.ApplyCalibration(calibration);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public AreaCalibration? CalibrateCurrentArea(IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements)
    {
        if (CurrentAreaKey is not { } key || placements is null || placements.Count < 2)
            return null;

        var refs = placements
            .Select(p => new LandmarkCalibrationSolver.Reference(p.World.X, p.World.Z, p.Pixel))
            .ToList();

        var calibration = LandmarkCalibrationSolver.Solve(refs);
        if (calibration is null) return null;

        _settings.AreaCalibrations[key] = calibration;
        _saver.Touch(); // AreaCalibrations is a sibling object — no PropertyChanged.
        _projector.ApplyCalibration(calibration);
        Changed?.Invoke(this, EventArgs.Empty);
        return calibration;
    }

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
