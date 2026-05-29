namespace Mithril.Tools.MapCalibrationWpf.Services;

using System.IO;
using System.Text.Json;

/// <summary>
/// Lists area names known to <c>landmarks.json</c>. The tool only offers
/// areas the data files actually mention; an area with no landmarks AND no
/// NPCs can't be calibrated.
///
/// <para>v1 reads landmarks alone — every populated PG area has at least one
/// landmark, and NPC-only areas would need separate UX (no anchor type to
/// click) which is out of scope for Phase 1.</para>
/// </summary>
public sealed class AreaCatalog
{
    public IReadOnlyList<string> ListAreasWithData(string landmarksJsonPath)
    {
        using var stream = File.OpenRead(landmarksJsonPath);
        using var doc = JsonDocument.Parse(stream);
        var areas = new List<string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array
                && prop.Value.GetArrayLength() > 0)
            {
                areas.Add(prop.Name);
            }
        }
        areas.Sort(StringComparer.Ordinal);
        return areas;
    }
}
