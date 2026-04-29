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
}
