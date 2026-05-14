namespace Mithril.Reference.Models.Misc;

/// <summary>
/// One landmark from <c>landmarks.json</c>. The file shape is
/// <c>Dictionary&lt;string, IReadOnlyList&lt;Landmark&gt;&gt;</c> keyed by
/// <c>AreaName</c>.
/// </summary>
public sealed class Landmark
{
    public string? Name { get; set; }
    public string? Desc { get; set; }

    /// <summary>Position string (typically <c>"x:N y:N z:N"</c>); parse at consumption time.</summary>
    public string? Loc { get; set; }

    /// <summary>
    /// Landmark category. Always populated on every landmark in the bundled corpus; one of
    /// <c>Portal</c> (180/280), <c>MeditationPillar</c> (55/280), <c>TeleportationPlatform</c>
    /// (45/280). Drives the per-area grouped rendering on Area-detail.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// The four-digit meditation-pillar combo (e.g. <c>"4017"</c>). Populated only when
    /// <see cref="Type"/> is <c>"MeditationPillar"</c> (55 entries in the bundled corpus);
    /// <see langword="null"/> for portals and teleportation platforms.
    /// </summary>
    public string? Combo { get; set; }
}
