using System.Text.Json;
using System.Text.Json.Nodes;
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

/// <summary>
/// Reads and writes <c>src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json</c>.
/// Uses <see cref="JsonObject"/> rather than the internal
/// <c>MapCalibrationJsonContext</c> so the tool can preserve the <c>$schema</c>
/// pointer and any unknown future fields without depending on internals of the
/// referenced project.
///
/// <para>Writes JSON in the exact shape <c>BundledBaselineLoader</c> reads:
/// camelCase property names, indented, default values omitted.</para>
/// </summary>
internal static class BaselineFile
{
    public static void UpsertAnchor(string baselinePath, string area, AreaCalibration cal)
    {
        if (!File.Exists(baselinePath))
        {
            throw new UserFacingException($"baseline JSON not found: {baselinePath}");
        }

        var text = File.ReadAllText(baselinePath);
        var root = (JsonNode.Parse(text) as JsonObject)
            ?? throw new UserFacingException($"baseline JSON at {baselinePath} is not an object");

        if (root["anchors"] is not JsonObject anchors)
        {
            anchors = new JsonObject();
            root["anchors"] = anchors;
        }

        anchors[area] = SerializeAreaCalibration(cal);

        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // ToJsonString already preserves property order so we don't need a
            // custom comparer; the bundled-loader doesn't care about order.
        });
        File.WriteAllText(baselinePath, json + Environment.NewLine);
    }

    private static JsonObject SerializeAreaCalibration(AreaCalibration cal)
    {
        // Mirror MapCalibrationJsonContext's DefaultIgnoreCondition = WhenWritingDefault.
        // Defaults per AreaCalibration.cs: MirrorNorth=false, CalibrationZoom=1.0,
        // Source=UserRefinement, SchemaVersion=1.
        var obj = new JsonObject
        {
            ["scale"] = cal.Scale,
            ["rotationRadians"] = cal.RotationRadians,
            ["originX"] = cal.OriginX,
            ["originY"] = cal.OriginY,
            ["referenceCount"] = cal.ReferenceCount,
            ["residualPixels"] = cal.ResidualPixels,
        };
        if (cal.MirrorNorth) obj["mirrorNorth"] = true;
        if (Math.Abs(cal.CalibrationZoom - 1.0) > 1e-9) obj["calibrationZoom"] = cal.CalibrationZoom;
        if (cal.Source != CalibrationSource.UserRefinement) obj["source"] = cal.Source.ToString();
        if (cal.SchemaVersion != 1) obj["schemaVersion"] = cal.SchemaVersion;
        return obj;
    }
}
