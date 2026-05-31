namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Persisted map-capture state: the single overlay/bbox rect in desktop pixels
/// (spec §7 — the capture region and the overlay rect are the same rect). Module
/// independent (the Capture engine is shared infra), so it lives in its own
/// settings file via the standard <c>ISettingsStore&lt;T&gt;</c> pattern, not in
/// <c>LegolasSettings</c>.
/// </summary>
public sealed class MapCaptureSettings
{
    /// <summary>
    /// Persisted-schema version (memory: <c>json_should_be_versioned</c>). Stamp
    /// 1 on the new root for cheap forward-compat.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// The desktop-pixel map bbox, or <see langword="null"/> when the user has
    /// not framed one yet (gates the auto-attempt, spec §10/§11).
    /// </summary>
    public CaptureRect? MapBbox { get; set; }
}
