using System;
using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mithril.MapCalibration.DependencyInjection;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibration.Common;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;
using DetectionMapRect = Mithril.MapCalibration.Detection.MapRect;

namespace Mithril.Tools.MapCalibration.Harness.Tests;

/// <summary>
/// #938 registration prototype (tools test only): evaluate OpenCV screenshot↔texture
/// matching for a precise + fast map-region locator. Validated against the
/// hand-verified ground-truth rects.
/// </summary>
public sealed class OpenCvRegistrationProbe
{
    private const string Area = "AreaEltibule";
    private static readonly string AssetCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mithril", "assets");
    private static string FrameDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private readonly ITestOutputHelper _out;
    public OpenCvRegistrationProbe(ITestOutputHelper output) => _out = output;

    [Fact]
    public void OpenCv_native_runtime_loads()
    {
        using var a = new Mat(16, 16, MatType.CV_32FC1, Scalar.All(1));
        using var b = new Mat(16, 16, MatType.CV_32FC1, Scalar.All(1));
        using var window = new Mat();
        var shift = Cv2.PhaseCorrelate(a, b, window, out double response);
        _out.WriteLine($"OpenCvSharp loaded; PhaseCorrelate(identical) = ({shift.X:0.###},{shift.Y:0.###}) resp={response:0.###}");
    }

    /// <summary>
    /// ECC (Enhanced Correlation Coefficient) sub-pixel affine alignment of the base
    /// texture into the screenshot, seeded from the coarse MapRectLocator. The warp
    /// maps template(=resized texture) coords → input(=frame) coords, so the texture
    /// origin maps to the map bbox origin and the warp scale gives the bbox size.
    /// </summary>
    [SkippableTheory]
    [InlineData("eltibule-frame1-rejected-3inliers.gray.png", 204, 133, 847, 841)]
    [InlineData("eltibule-frame2-accepted-7.61px.gray.png", 130, 60, 995, 986)]
    public void Ecc_registration_vs_ground_truth(string frameFile, int gtX, int gtY, int gtW, int gtH)
    {
        var framePath = Path.Combine(FrameDir, frameFile);
        Skip.IfNot(File.Exists(framePath), $"frame fixture missing: {framePath}");
        Skip.IfNot(File.Exists(Path.Combine(AssetCacheDir, $"map-texture-{Area}.bin")),
            $"base-texture cache missing under {AssetCacheDir}");
        using var sp = new ServiceCollection().AddMithrilMapCalibrationEngine(AssetCacheDir).BuildServiceProvider();
        var baseTex = sp.GetRequiredService<IBaseTextureProvider>().TryGetBaseTexture(Area);
        Skip.If(baseTex is null, "base texture failed to load");
        var frame = ImageIo.LoadGray(framePath);

        var seed = MapRectLocator.AutoDetect(frame, baseTex!, 0.5, MapRectLocator.DefaultWorkingLongEdgePx);
        Skip.If(seed is null, "seed locate failed");
        int sw = seed!.Width, sh = seed.Height;

        using var texFull = ToMat32F(baseTex!);
        using var tmpl = new Mat();
        Cv2.Resize(texFull, tmpl, new Size(sw, sh));            // texture at coarse map scale = ECC template
        using var input = ToMat32F(frame);

        using var warp = Mat.Eye(2, 3, MatType.CV_32FC1).ToMat();
        warp.Set(0, 2, (float)seed.OriginX);                    // init: template origin → seed origin in frame
        warp.Set(1, 2, (float)seed.OriginY);

        var crit = new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 200, 1e-6);
        var sw2 = Stopwatch.StartNew();
        double ecc;
        try
        {
            ecc = Cv2.FindTransformECC(tmpl, input, warp, MotionTypes.Affine, crit, null, 5);
        }
        catch (OpenCVException ex)
        {
            _out.WriteLine($"{frameFile}: ECC did NOT converge from seed ({seed.OriginX},{seed.OriginY}) {sw}x{sh}: {ex.Message}");
            throw;
        }
        sw2.Stop();

        float a = warp.At<float>(0, 0), b = warp.At<float>(0, 1), tx = warp.At<float>(0, 2);
        float c = warp.At<float>(1, 0), d = warp.At<float>(1, 1), ty = warp.At<float>(1, 2);
        // Full texture maps to the frame at this affine; bbox = warp applied to the resized-template extent.
        double scaleX = Math.Sqrt(a * a + c * c);
        double scaleY = Math.Sqrt(b * b + d * d);
        int rw = (int)Math.Round(sw * scaleX), rh = (int)Math.Round(sh * scaleY);
        int rx = (int)Math.Round(tx), ry = (int)Math.Round(ty);

        _out.WriteLine(
            $"{frameFile}: seed ({seed.OriginX},{seed.OriginY}) {sw}x{sh} → ECC ({rx},{ry}) {rw}x{rh} " +
            $"ecc={ecc:0.000} scale=({scaleX:0.0000},{scaleY:0.0000}) shear=({a:0.000},{b:0.000},{c:0.000},{d:0.000})  " +
            $"gt ({gtX},{gtY}) {gtW}x{gtH}  delta=({rx - gtX},{ry - gtY},{rw - gtW})  {sw2.ElapsedMilliseconds} ms");

        Math.Abs(rx - gtX).Should().BeLessThanOrEqualTo(3);
        Math.Abs(ry - gtY).Should().BeLessThanOrEqualTo(3);
        Math.Abs(rw - gtW).Should().BeLessThanOrEqualTo(4);

        // End-to-end: the ECC rect must drive a good detect→solve (the actual goal).
        var templates = sp.GetRequiredService<IIconTemplateProvider>().GetTemplates();
        var crop = ImageOps.Crop(frame, rx, ry, rw, rh);
        var texR = ImageOps.Resize(baseTex!, rw, rh);
        var rect = new DetectionMapRect(0, 0, rw, rh, baseTex!.Width, baseTex.Height);
        var blob = new BlobOptions(MinArea: 12, MaxIconArea: 900, MinSolidity: 0.35, MaxAspect: 2.5, MinPeak: 0.7);
        var req = new DetectionRequest(crop, texR, rect, templates, RimMaskMode.DeviationFlood, 0.5, 0.80, blob) { RenderSizePx = 16 };
        var result = new MapCalibrationSolveEngine(new DeviationBlobCalibrationDetector(), new CalibrationConfidenceGate())
            .Solve(req, EltibuleLiveFrameDetectionRepro.EltibuleReferences());
        if (result.Calibration is not null)
            _out.WriteLine($"  ECC-rect solve: ACCEPTED — {result.InlierCount} inliers, residual {result.Calibration.ResidualPixels:0.00} px");
        else
            _out.WriteLine($"  ECC-rect solve: REJECTED — {result.InlierCount} inliers ({result.RejectReason})");
    }

    private static Mat ToMat32F(GrayImage g)
    {
        using var m8 = Mat.FromPixelData(g.Height, g.Width, MatType.CV_8UC1, g.Pixels);
        var m = new Mat();
        m8.ConvertTo(m, MatType.CV_32FC1);
        return m;
    }
}
