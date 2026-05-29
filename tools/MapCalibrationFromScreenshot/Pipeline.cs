using System.Globalization;
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

/// <summary>
/// Glue layer that wires the seven pipeline phases together. Each phase delegates
/// to a focused helper; <see cref="Pipeline"/> only sequences them and prints
/// per-step progress so a user running the tool can see where time is spent
/// (icon extraction + the NCC pass dominate end-to-end).
/// </summary>
internal static class Pipeline
{
    public static int Run(CliArgs args)
    {
        if (args.Phase == Phase.SelfTest)
        {
            return SelfTest.Run();
        }

        var pgInstall = SteamInstall.FindPgInstall();
        Console.WriteLine($"[steam] PG install: {pgInstall}");

        var iconsDir = args.IconsDir ?? DefaultScratch("icons");
        var mapDir = args.MapDir ?? DefaultScratch("maps");
        var tpkPath = args.TpkPath ?? Path.Combine(AppContext.BaseDirectory, "classdata.tpk");

        // Phase 1: icon templates. Always ensure they exist (cheap to skip if cached).
        IconTemplateExtractor.EnsureExtracted(pgInstall, iconsDir, tpkPath);

        if (args.Phase == Phase.ExtractIcons)
        {
            Console.WriteLine($"[done] icon templates ready in {iconsDir}");
            return 0;
        }

        // Phase 2: area map texture.
        var mapPng = MapTextureExtractor.EnsureExtracted(pgInstall, mapDir, args.Area);

        if (args.Phase == Phase.ExtractMap)
        {
            Console.WriteLine($"[done] map texture at {mapPng}");
            return 0;
        }

        if (!File.Exists(args.ScreenshotPath))
        {
            throw new UserFacingException($"screenshot not found: {args.ScreenshotPath}");
        }

        // Phase 3-6: locate, detect, assign, solve. ScreenshotCalibrator owns the
        // image-processing math so Pipeline can stay flow-only.
        var inputs = new CalibrationInputs(
            ScreenshotPath: args.ScreenshotPath,
            AreaMapPath: mapPng,
            IconsDir: iconsDir,
            LandmarksJsonPath: ResolveLandmarksPath(args),
            Area: args.Area,
            Zoom: args.Zoom,
            PlayerCoord: ResolvePlayerCoord(args));

        var result = ScreenshotCalibrator.Calibrate(inputs);
        if (result.Calibration is null)
        {
            Console.Error.WriteLine($"[fail] could not solve: {result.FailureReason}");
            return 1;
        }

        Console.WriteLine();
        PrintCalibrationSummary(result.Calibration, result.AssignedReferences);

        if (args.DryRun)
        {
            Console.WriteLine("[dry-run] baseline JSON unchanged");
            return 0;
        }

        // Phase 7: persist.
        var baselinePath = ResolveBaselinePath(args);
        BaselineFile.UpsertAnchor(baselinePath, args.Area, result.Calibration);
        Console.WriteLine($"[ok] wrote anchors[{args.Area}] -> {baselinePath}");
        return 0;
    }

    private static (double X, double Z)? ResolvePlayerCoord(CliArgs args)
    {
        if (args.PlayerCoord is { } c) return c;
        if (args.PlayerLogPath is null) return null;
        var coord = PlayerLogScanner.MostRecentPositionInArea(args.PlayerLogPath, args.Area)
            ?? throw new UserFacingException(
                $"--player-log given but no recent [Status] line found in {args.PlayerLogPath} for area {args.Area}");
        Console.WriteLine($"[player-log] resolved {args.Area} position: ({coord.X:0.##}, {coord.Z:0.##})");
        return (coord.X, coord.Z);
    }

    private static string ResolveBaselinePath(CliArgs args)
    {
        if (args.BaselinePath is not null) return args.BaselinePath;
        var p = Path.Combine(RepoRoot(), "src", "Mithril.MapCalibration", "BundledData", "map-calibration-baseline.json");
        if (!File.Exists(p))
        {
            throw new UserFacingException($"default baseline path not found: {p} (pass --baseline)");
        }
        return p;
    }

    private static string ResolveLandmarksPath(CliArgs args)
    {
        if (args.LandmarksPath is not null) return args.LandmarksPath;
        var p = Path.Combine(RepoRoot(), "src", "Mithril.Shared", "Reference", "BundledData", "landmarks.json");
        if (!File.Exists(p))
        {
            throw new UserFacingException($"default landmarks.json path not found: {p} (pass --landmarks)");
        }
        return p;
    }

    private static string RepoRoot()
    {
        // Climb from the tool's bin dir up to the repo root (it has Mithril.slnx).
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "Mithril.slnx"))) return dir;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new UserFacingException(
            $"could not locate Mithril.slnx walking up from {AppContext.BaseDirectory}");
    }

    private static string DefaultScratch(string sub)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mithril-852", sub);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PrintCalibrationSummary(AreaCalibration cal, IReadOnlyList<AssignedReference> refs)
    {
        Console.WriteLine($"[solve] residualPixels={cal.ResidualPixels:0.00}  scale={cal.Scale:0.0000} px/unit  rotation={cal.RotationRadians:0.000} rad  origin=({cal.OriginX:0.0},{cal.OriginY:0.0})  mirrorNorth={cal.MirrorNorth}");
        Console.WriteLine($"[solve] {cal.ReferenceCount} references used:");
        foreach (var r in refs)
        {
            Console.WriteLine($"        {r.Label,-32} world=({r.WorldX:0.0},{r.WorldZ:0.0})  pixel=({r.PixelX:0.0},{r.PixelY:0.0})  score={r.MatchScore:0.000}");
        }
        if (cal.ResidualPixels >= 12.0)
        {
            Console.WriteLine($"[warn] residualPixels={cal.ResidualPixels:0.00} >= 12.0 (CalibrationGoodResidualPx). Result is below shipping bar.");
        }
    }
}

/// <summary>Bundle of everything <see cref="ScreenshotCalibrator"/> needs.</summary>
internal sealed record CalibrationInputs(
    string ScreenshotPath,
    string AreaMapPath,
    string IconsDir,
    string LandmarksJsonPath,
    string Area,
    double Zoom,
    (double X, double Z)? PlayerCoord);

internal sealed record AssignedReference(
    string Label,
    double WorldX,
    double WorldZ,
    double PixelX,
    double PixelY,
    double MatchScore);

internal sealed record CalibrationResult(
    AreaCalibration? Calibration,
    IReadOnlyList<AssignedReference> AssignedReferences,
    string? FailureReason);
