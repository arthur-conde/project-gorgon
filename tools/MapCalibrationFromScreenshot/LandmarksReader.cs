using System.Text.Json;
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

/// <summary>
/// Reads the subset of <c>landmarks.json</c> we need: per-area list of
/// (Type, Name, world Loc). The Mithril.Reference deserialiser pulls in more
/// fields, but for v1 the tool only needs the discriminator + position to feed
/// the solver — no point in dragging the whole reference-data project graph
/// into this tool's build.
/// </summary>
internal static class LandmarksReader
{
    public static IReadOnlyList<LandmarkRef> LoadForArea(string landmarksJsonPath, string area)
    {
        if (!File.Exists(landmarksJsonPath))
        {
            throw new UserFacingException($"landmarks.json not found: {landmarksJsonPath}");
        }

        using var stream = File.OpenRead(landmarksJsonPath);
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty(area, out var areaArray))
        {
            return [];
        }

        var list = new List<LandmarkRef>();
        foreach (var entry in areaArray.EnumerateArray())
        {
            var type = entry.TryGetProperty("Type", out var t) ? t.GetString() : null;
            var name = entry.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var loc = entry.TryGetProperty("Loc", out var l) ? l.GetString() : null;
            if (type is null || loc is null) continue;
            var world = WorldCoord.TryParse(loc);
            if (world is null) continue;
            list.Add(new LandmarkRef(type, name ?? "(unnamed)", world.Value));
        }
        return list;
    }
}

internal sealed record LandmarkRef(string Type, string Name, WorldCoord World);
