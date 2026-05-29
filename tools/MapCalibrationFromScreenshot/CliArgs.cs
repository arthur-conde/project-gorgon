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
    string? IconsDir,
    string? MapDir,
    string? TpkPath,
    string? PlayerLogPath,
    (double X, double Z)? PlayerCoord,
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
        string? iconsDir = null;
        string? mapDir = null;
        string? tpk = null;
        string? playerLog = null;
        (double, double)? playerCoord = null;
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
                case "--icons-dir": iconsDir = Next(argv, ref i); break;
                case "--map-dir": mapDir = Next(argv, ref i); break;
                case "--tpk": tpk = Next(argv, ref i); break;
                case "--player-log": playerLog = Next(argv, ref i); break;
                case "--player-coord":
                    playerCoord = ParseCoord(Next(argv, ref i));
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

        if (screenshot is null && phase is not (Phase.ExtractIcons or Phase.SelfTest))
        {
            Console.Error.WriteLine("--screenshot required (except --phase extract-icons / self-test)");
            return null;
        }
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
            IconsDir: iconsDir,
            MapDir: mapDir,
            TpkPath: tpk,
            PlayerLogPath: playerLog,
            PlayerCoord: playerCoord,
            Zoom: zoom,
            Phase: phase,
            DryRun: dryRun);
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

            paths (sane defaults):
              --baseline    <baseline.json> default: src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json
              --landmarks   <landmarks.json> default: src/Mithril.Shared/Reference/BundledData/landmarks.json
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
