using System.Globalization;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

/// <summary>
/// Parsed command-line arguments. All optional except <see cref="ScreenshotPath"/>
/// and <see cref="Area"/>. The tool needs either an explicit
/// <see cref="PlayerCoord"/> or a <see cref="PlayerLogPath"/> to triangulate the
/// player-pin's world coord; if neither is supplied the solver still runs but
/// loses the most reliable reference point.
/// </summary>
internal sealed record CliArgs(
    string ScreenshotPath,
    string Area,
    string? BaselinePath,
    string? LandmarksPath,
    string? NpcsPath,
    string? IconsDir,
    string? MapDir,
    string? TpkPath,
    string? PlayerLogPath,
    (double X, double Z)? PlayerCoord,
    (int X, int Y, int W, int H)? MapRect,
    double DetectionThreshold,
    int IconRenderSize,
    string? DebugImagePath,
    string? ProjectionOverlayPath,
    double Zoom,
    Phase Phase,
    bool DryRun)
{
    public static CliArgs? Parse(string[] argv)
    {
        if (argv.Length == 0) return null;

        string? screenshot = null;
        string? area = null;
        string? baseline = null;
        string? landmarks = null;
        string? npcs = null;
        string? iconsDir = null;
        string? mapDir = null;
        string? tpk = null;
        string? playerLog = null;
        (double, double)? playerCoord = null;
        (int, int, int, int)? mapRect = null;
        double detectionThreshold = 0.5;
        int iconRenderSize = 0;  // 0 = auto-detect
        string? debugImagePath = null;
        string? projectionOverlayPath = null;
        double zoom = 1.0;
        Phase phase = Phase.Full;
        bool dryRun = false;

        for (int i = 0; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "--screenshot": screenshot = Next(argv, ref i); break;
                case "--area": area = Next(argv, ref i); break;
                case "--baseline": baseline = Next(argv, ref i); break;
                case "--landmarks": landmarks = Next(argv, ref i); break;
                case "--npcs": npcs = Next(argv, ref i); break;
                case "--icons-dir": iconsDir = Next(argv, ref i); break;
                case "--map-dir": mapDir = Next(argv, ref i); break;
                case "--tpk": tpk = Next(argv, ref i); break;
                case "--player-log": playerLog = Next(argv, ref i); break;
                case "--player-coord":
                    playerCoord = ParseCoord(Next(argv, ref i));
                    break;
                case "--map-rect":
                    mapRect = ParseMapRect(Next(argv, ref i));
                    break;
                case "--detection-threshold":
                    detectionThreshold = double.Parse(Next(argv, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--icon-render-size":
                    iconRenderSize = int.Parse(Next(argv, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--debug-image":
                    debugImagePath = Next(argv, ref i);
                    break;
                case "--projection-overlay":
                    projectionOverlayPath = Next(argv, ref i);
                    break;
                case "--zoom":
                    zoom = double.Parse(Next(argv, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--phase":
                    phase = ParsePhase(Next(argv, ref i));
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "-h" or "--help":
                    return null;
                default:
                    Console.Error.WriteLine($"unknown arg '{argv[i]}'");
                    return null;
            }
        }

        // Only the full pipeline needs --screenshot. extract-icons and
        // extract-map are cache-population modes; self-test synthesises its
        // own inputs.
        if (screenshot is null && phase is Phase.Full)
        {
            Console.Error.WriteLine("--screenshot required for --phase full");
            return null;
        }
        // --area is needed whenever we touch area-specific data (the map
        // bundle in extract-map; landmarks/npcs in the full pipeline).
        // extract-icons (sharedassets0-wide) and self-test (synthetic area)
        // don't need it.
        if (area is null && phase is not (Phase.ExtractIcons or Phase.SelfTest))
        {
            Console.Error.WriteLine("--area required (e.g. --area AreaSerbule)");
            return null;
        }

        return new CliArgs(
            ScreenshotPath: screenshot ?? "",
            Area: area ?? "",
            BaselinePath: baseline,
            LandmarksPath: landmarks,
            NpcsPath: npcs,
            IconsDir: iconsDir,
            MapDir: mapDir,
            TpkPath: tpk,
            PlayerLogPath: playerLog,
            PlayerCoord: playerCoord,
            MapRect: mapRect,
            DetectionThreshold: detectionThreshold,
            IconRenderSize: iconRenderSize,
            DebugImagePath: debugImagePath,
            ProjectionOverlayPath: projectionOverlayPath,
            Zoom: zoom,
            Phase: phase,
            DryRun: dryRun);
    }

    private static (int, int, int, int) ParseMapRect(string s)
    {
        var parts = s.Split(',', 4);
        if (parts.Length != 4)
        {
            throw new UserFacingException($"--map-rect wants 'x,y,w,h' (got '{s}')");
        }
        return (
            int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
            int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
            int.Parse(parts[2].Trim(), CultureInfo.InvariantCulture),
            int.Parse(parts[3].Trim(), CultureInfo.InvariantCulture));
    }

    private static string Next(string[] argv, ref int i)
    {
        if (i + 1 >= argv.Length)
        {
            throw new UserFacingException($"missing value after '{argv[i]}'");
        }
        return argv[++i];
    }

    private static (double, double) ParseCoord(string s)
    {
        var parts = s.Split(',', 2);
        if (parts.Length != 2)
        {
            throw new UserFacingException($"--player-coord wants 'x,z' (got '{s}')");
        }
        var x = double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
        var z = double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
        return (x, z);
    }

    private static Phase ParsePhase(string s) => s.ToLowerInvariant() switch
    {
        "extract-icons" => Phase.ExtractIcons,
        "extract-map" => Phase.ExtractMap,
        "full" => Phase.Full,
        "self-test" => Phase.SelfTest,
        _ => throw new UserFacingException($"unknown phase '{s}' (extract-icons | extract-map | full | self-test)"),
    };

    public static void PrintUsage()
    {
        Console.Error.WriteLine("""
            mithril#852 — screenshot-based map calibration

            usage:
              MapCalibrationFromScreenshot --screenshot <png> --area <AreaName> [opts]
              MapCalibrationFromScreenshot --phase extract-icons [--tpk <path>] [--icons-dir <dir>]

            required:
              --screenshot <path>           PNG of the in-game map screen (M key)
              --area <AreaName>             e.g. AreaSerbule, matches landmarks.json key

            recommended (improves the fit):
              --player-coord <x,z>          player's world coord at screenshot time (signed)
              --player-log  <Player.log>    alternative: extract from most recent [Status]
              --zoom <float>                in-game map zoom (default 1.0 = CalibrationZoom default)

            map-rect override (skip auto-detect):
              --map-rect <x,y,w,h>          visible map's bbox in the screenshot (px); use when
                                            auto-detect can't find the map or picks the wrong scale

            detection tuning:
              --detection-threshold <0..1>  min NCC score to accept a template match (default 0.5).
                                            Lower (e.g. 0.3) when real-screenshot recall is low;
                                            higher (e.g. 0.7) when there are too many false positives
              --icon-render-size <px>       on-screen pixel size PG renders icons at; bypasses the
                                            auto-detect sweep. Measure by opening the screenshot in
                                            an image viewer and reading an icon's bbox dimension
                                            (e.g. PG renders ~19 px at 1080p UI scale)
              --debug-image <path>          write an annotated PNG: cyan rects mark every detection
                                            that cleared threshold, red crosses mark the pivot-
                                            corrected anchor, green rect outlines the map rect
              --projection-overlay <path>   project every landmark + NPC ref through the recovered
                                            calibration and mark on the screenshot. Yellow crosses
                                            for all refs, green rects around RANSAC inliers. Useful
                                            for seeing where the residual lives at a glance

            paths (sane defaults):
              --baseline    <baseline.json> default: src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json
              --landmarks   <landmarks.json> default: src/Mithril.Shared/Reference/BundledData/landmarks.json
              --npcs        <npcs.json>      default: src/Mithril.Shared/Reference/BundledData/npcs.json
              --icons-dir   <dir>           where to read/cache extracted icon PNGs
              --map-dir     <dir>           where to read/cache extracted area-map PNGs
              --tpk         <classdata.tpk> AssetsTools.NET class-data package (~290 KB)
                                            (download from github.com/nesrak1/UABEA/raw/master/ReleaseFiles/classdata.tpk)

            modes:
              --phase extract-icons         only extract the icon templates from sharedassets0.assets
              --phase extract-map           only extract the area's map PNG from its bundle
              --phase full                  (default) run the full pipeline
              --phase self-test             synthetic end-to-end test (no PG/tpk needed)
              --dry-run                     don't write the baseline JSON, just print what would change
            """);
    }
}

internal enum Phase
{
    Full,
    ExtractIcons,
    ExtractMap,
    SelfTest,
}
