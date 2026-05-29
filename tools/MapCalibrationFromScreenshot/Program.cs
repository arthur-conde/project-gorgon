// Offline tool for mithril#852: derive per-area map calibration from a single
// in-game screenshot of the open map. Reads on-disk PG assets only — never
// touches the running game process. Output: a populated
// `anchors["Area<Name>"] = AreaCalibration{…}` entry written back into
// `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json`.
//
// Pipeline (all phases run unless --phase <name> selects one):
//   1. extract-icons  — pull landmark + player-pin Texture2Ds from sharedassets0.assets
//   2. extract-map    — pull the Map_Area<Name> Texture2D from the area's bundle
//   3. locate-map     — find the map's rect within the screenshot
//   4. detect         — NCC-match each icon template against the screenshot
//   5. assign         — match detections to landmarks.json entries by Type
//   6. solve          — feed (world, pixel) pairs into LandmarkCalibrationSolver
//   7. persist        — write the AreaCalibration into the baseline JSON
//
// See README.md next to this file for usage and the synthetic test harness.

using Mithril.Tools.MapCalibration.Common;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var parsed = CliArgs.Parse(args);
            if (parsed is null)
            {
                CliArgs.PrintUsage();
                return 2;
            }
            return Pipeline.Run(parsed);
        }
        catch (UserFacingException ex)
        {
            // Expected failure mode (missing PG install, missing screenshot,
            // unreadable log, etc.). Surface message only — stack traces here
            // are noise.
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"unexpected: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}
