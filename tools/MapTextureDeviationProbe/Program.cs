using System.Drawing;
using System.Globalization;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;

// MapTextureDeviationProbe — R&D prototype for mithril#897 (gate study, task 2).
//
// Idea: the base map texture (Map_<Area>.v4.png) is icon-free; the in-game map
// screenshot has icons (and fog) drawn on top of the same artwork. Align the
// texture to the screenshot extent, then for every pixel compute a LOCAL NCC
// between a screenshot patch and the aligned-texture patch. Terrain matches with
// high local NCC even though PG restyles/tints the in-game map (NCC is invariant
// to per-window linear brightness/contrast). An icon disrupts the local match ->
// low NCC -> "added content" candidate. The rocky border is identical in both ->
// high NCC -> excluded for free; fog-of-war is the smooth/large residual.
//
// This is a VISUALIZATION probe: it writes a deviation heatmap + an overlay so we
// can eyeball whether icons pop and the border doesn't, before wiring local-NCC
// into the real detector.
//
// Usage:
//   dotnet run --project tools/MapTextureDeviationProbe -- \
//     --screenshot <png> --texture <png> --out-dir <dir> \
//     [--window 11] [--low-ncc 0.5] [--orientation auto|0|180]
//
// --blobs adds the SHAPE/SIZE FILTER stage (mithril#897 remaining-work item 1):
// threshold the deviation map, connected-components label it, and classify each
// blob icon / fog / structure by area + solidity + aspect + peak-deviation. This
// turns the "all added content" deviation map into a clean icon-candidate set.
//   ... --blobs [--border-mask] [--close 1]
//       [--min-area 12] [--max-icon-area 900] [--min-solidity 0.35]
//       [--max-aspect 2.5] [--min-peak 0.7]
//       [--ground-truth --area AreaSerbule --landmarks <json> --npcs <json>
//        --baseline <json> [--gt-tol 20]]

string screenshotPath = Cli.Get(args, "--screenshot", "");
string texturePath = Cli.Get(args, "--texture", "");
string outDir = Cli.Get(args, "--out-dir", ".");
int window = int.Parse(Cli.Get(args, "--window", "11"));
double lowNcc = ParseInv(Cli.Get(args, "--low-ncc", "0.5"));
string orientationArg = Cli.Get(args, "--orientation", "auto"); // auto | 0 | 180

// --- blob shape/size filter options ---
bool doBlobs = Cli.Has(args, "--blobs");
bool useBorderMask = Cli.Has(args, "--border-mask");
int closeRadius = int.Parse(Cli.Get(args, "--close", "1"));
var blobOpts = new BlobOptions(
    MinArea: int.Parse(Cli.Get(args, "--min-area", "12")),
    MaxIconArea: int.Parse(Cli.Get(args, "--max-icon-area", "900")),
    MinSolidity: ParseInv(Cli.Get(args, "--min-solidity", "0.35")),
    MaxAspect: ParseInv(Cli.Get(args, "--max-aspect", "2.5")),
    MinPeak: ParseInv(Cli.Get(args, "--min-peak", "0.7")));
// --- blob typing: type-aware template NCC within icon blobs, emit detections CSV ---
string typeIconsDir = Cli.Get(args, "--icons-dir", "");
int typeRenderSize = int.Parse(Cli.Get(args, "--icon-render-size", "16"));
double typeFloor = ParseInv(Cli.Get(args, "--type-floor", "0.55"));
var iconSizeOverrides = new Dictionary<string, (int W, int H)>(StringComparer.Ordinal);
for (int ai = 0; ai < args.Length - 1; ai++)
{
    if (args[ai] != "--icon-size") continue;
    var spec = args[ai + 1];
    int eq = spec.IndexOf('='), xx = spec.IndexOf('x');
    if (eq > 0 && xx > eq)
        iconSizeOverrides[spec[..eq]] = (
            int.Parse(spec[(eq + 1)..xx], CultureInfo.InvariantCulture),
            int.Parse(spec[(xx + 1)..], CultureInfo.InvariantCulture));
}
// --- ground-truth overlap (Serbule only — the one area with a committed baseline) ---
bool groundTruth = Cli.Has(args, "--ground-truth");
string gtArea = Cli.Get(args, "--area", "");
string landmarksPath = Cli.Get(args, "--landmarks", "");
string npcsPath = Cli.Get(args, "--npcs", "");
string baselinePath = Cli.Get(args, "--baseline", "");
double gtTol = ParseInv(Cli.Get(args, "--gt-tol", "20"));

if (screenshotPath.Length == 0 || texturePath.Length == 0)
{
    Console.Error.WriteLine("required: --screenshot <png> --texture <png>");
    return 1;
}
Directory.CreateDirectory(outDir);
string stem = Path.GetFileNameWithoutExtension(screenshotPath);

Console.WriteLine($"screenshot: {screenshotPath}");
Console.WriteLine($"texture:    {texturePath}");
Console.WriteLine($"window={window}  low-ncc={lowNcc}  orientation={orientationArg}");

// Use the shared loader (ImageIo.LoadBgra) so we inherit its DPI fix — the very
// bug that derailed the gate study. It draws into an explicit pixel-sized rect.
var (shotBgra, shotW, shotH) = ImageIo.LoadBgra(screenshotPath);
var (texBgra, texW, texH) = ImageIo.LoadBgra(texturePath);
BgraImage shot = new BgraImage(shotW, shotH, shotBgra);
BgraImage tex = new BgraImage(texW, texH, texBgra);
Console.WriteLine($"screenshot {shot.Width}x{shot.Height}  texture {tex.Width}x{tex.Height}");

// --- rim-from-texture experiment: run BorderMask on screenshot vs texture and
//     compare interior bleed / masked fraction. Answers "can the rim be detected
//     from the icon-free texture alone, and is it cleaner than the screenshot?" ---
if (Cli.Has(args, "--rim-debug"))
{
    void DumpRim(byte[] bgra, int rw, int rh, string label, string file)
    {
        var mask = BorderMask.Compute(bgra, rw, rh, 4);
        int masked = 0;
        var viz = (byte[])bgra.Clone();
        for (int p = 0; p < rw * rh; p++)
        {
            if (!mask[p]) continue;
            masked++;
            int o = p * 4;
            viz[o] = (byte)(viz[o] / 2); viz[o + 1] = (byte)(viz[o + 1] / 2);
            viz[o + 2] = (byte)(128 + viz[o + 2] / 2); viz[o + 3] = 255;
        }
        var path = Path.Combine(outDir, file);
        ImageIo.SaveBgraPng(viz, rw, rh, path);
        Console.WriteLine($"[rim-debug] {label}: masked {masked}/{rw * rh} ({(double)masked / (rw * rh):P1}) -> {path}");
    }
    DumpRim(shot.Pixels, shot.Width, shot.Height, "screenshot", $"{stem}_rim_screenshot.png");
    DumpRim(tex.Pixels, tex.Width, tex.Height, "texture", $"{stem}_rim_texture.png");
}

// --- INPUT SANITY (the lesson from the DPI bug: verify pixels before trusting metrics) ---
double shotMean = Gray.MeanLuma(shot);
Console.WriteLine($"screenshot mean luma = {shotMean:F1} (near-0 => likely a load/DPI problem)");
if (shotMean < 8) Console.Error.WriteLine("WARNING: screenshot is almost black — check the load path before trusting results.");

// Grayscale + resize texture to the screenshot extent (same map, same extent when zoomed fully out).
float[] shotG = Gray.ToFloat(shot);

float[] BuildAlignedTexture(bool rotate180)
{
    using var bmp = Gray.ToGrayBitmap(tex);
    using var dst = new Bitmap(shot.Width, shot.Height);
    using (var g = Graphics.FromImage(dst))
    {
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        if (rotate180)
        {
            g.TranslateTransform(shot.Width, shot.Height);
            g.RotateTransform(180);
        }
        g.DrawImage(bmp, new Rectangle(0, 0, shot.Width, shot.Height));
    }
    return Gray.FromGrayBitmap(dst);
}

// Decide orientation: PG draws the texture artwork un-rotated and only the
// world->pixel mapping flips for 180-deg areas, so screenshot==texture orientation
// is expected. But verify empirically by picking the orientation with higher mean NCC.
(double meanNcc, float[] dev) Run(bool rot180)
{
    float[] texG = BuildAlignedTexture(rot180);
    float[] devMap = LocalNcc.DeviationMap(shotG, texG, shot.Width, shot.Height, window, out double mean);
    return (mean, devMap);
}

float[] dev;
bool used180;
if (orientationArg == "0") { (var m0, dev) = Run(false); used180 = false; Console.WriteLine($"meanNCC@0  = {m0:F4}"); }
else if (orientationArg == "180") { (var m1, dev) = Run(true); used180 = true; Console.WriteLine($"meanNCC@180 = {m1:F4}"); }
else
{
    var (m0, d0) = Run(false);
    var (m1, d1) = Run(true);
    Console.WriteLine($"meanNCC@0 = {m0:F4}   meanNCC@180 = {m1:F4}");
    used180 = m1 > m0;
    dev = used180 ? d1 : d0;
    Console.WriteLine($"auto-selected orientation: {(used180 ? "180" : "0")} (higher mean NCC)");
}

// --- Stats: how much of the image is "deviating", and is the border quiet? ---
int n = shot.Width * shot.Height;
int lowCount = 0;
foreach (var v in dev) if (v >= 1.0 - lowNcc) lowCount++; // dev = 1-ncc; dev >= 1-lowNcc  <=>  ncc <= lowNcc
double lowFrac = (double)lowCount / n;
Console.WriteLine($"low-NCC pixels (ncc <= {lowNcc}): {lowCount}  ({lowFrac:P1} of image)");
Console.WriteLine($"border-band mean deviation: {BorderBandMeanDeviation(dev, shot.Width, shot.Height, 0.06):F3} (want LOW — border should match)");
Console.WriteLine($"interior   mean deviation: {InteriorMeanDeviation(dev, shot.Width, shot.Height, 0.06):F3}");

// --- deviation-flood rim mask: flood from the image edge through HIGH-deviation
//     pixels. The rim is a connected, edge-touching deviation band; interior icons
//     are isolated high-deviation islands in low-deviation matched terrain, and the
//     interior brown dirt the COLOUR flood wrongly ate is LOW-deviation (it matches
//     the base texture). So this masks the rim WITHOUT eating the interior — the fix
//     for the colour BorderMask's over-masking. Compare fractions vs the colour mask. ---
if (Cli.Has(args, "--rim-debug"))
{
    int W = shot.Width, H = shot.Height;
    double thr = 1.0 - lowNcc;
    var hi = new bool[W * H];
    for (int p = 0; p < W * H; p++) hi[p] = dev[p] >= thr;
    var rim = new bool[W * H];
    var q = new Queue<int>();
    void Enq(int x, int y)
    {
        if (x < 0 || x >= W || y < 0 || y >= H) return;
        int k = y * W + x;
        if (hi[k] && !rim[k]) { rim[k] = true; q.Enqueue(k); }
    }
    for (int x = 0; x < W; x++) { Enq(x, 0); Enq(x, H - 1); }
    for (int y = 0; y < H; y++) { Enq(0, y); Enq(W - 1, y); }
    while (q.Count > 0)
    {
        int k = q.Dequeue(); int x = k % W, y = k / W;
        Enq(x - 1, y); Enq(x + 1, y); Enq(x, y - 1); Enq(x, y + 1);
    }
    int masked = 0;
    var viz = (byte[])shot.Pixels.Clone();
    for (int p = 0; p < W * H; p++)
    {
        if (!rim[p]) continue;
        masked++;
        int o = p * 4;
        viz[o] = (byte)(viz[o] / 2); viz[o + 1] = (byte)(viz[o + 1] / 2);
        viz[o + 2] = (byte)(128 + viz[o + 2] / 2); viz[o + 3] = 255;
    }
    var devRimPath = Path.Combine(outDir, $"{stem}_rim_devflood.png");
    ImageIo.SaveBgraPng(viz, W, H, devRimPath);
    Console.WriteLine($"[rim-debug] deviation-flood (edge-connected dev>={thr:0.00}): masked {masked}/{W * H} ({(double)masked / (W * H):P1}) -> {devRimPath}");
}

// --- Outputs ---
string heatPath = Path.Combine(outDir, $"{stem}_deviation.png");
string overlayPath = Path.Combine(outDir, $"{stem}_overlay.png");
{ var hm = Heatmap(dev, shot.Width, shot.Height); ImageIo.SaveBgraPng(hm.Pixels, hm.Width, hm.Height, heatPath); }
{ var ov = Overlay(shot, dev, lowNcc); ImageIo.SaveBgraPng(ov.Pixels, ov.Width, ov.Height, overlayPath); }
Console.WriteLine($"wrote {heatPath}");
Console.WriteLine($"wrote {overlayPath}");

// --- SHAPE/SIZE FILTER stage (--blobs) ---
if (doBlobs)
{
    var gt = groundTruth
        ? new GroundTruthInputs(gtArea, landmarksPath, npcsPath, baselinePath, gtTol, texW, texH)
        : null;
    BlobTypingInputs? typing = typeIconsDir.Length > 0
        ? new BlobTypingInputs(typeIconsDir, typeRenderSize, iconSizeOverrides, typeFloor,
            Path.Combine(outDir, $"{stem}_typed_detections.csv"))
        : null;
    GrayImage? shotGray = typing is not null ? ImageIo.LoadGray(screenshotPath) : null;
    BlobStage.Run(shot, dev, lowNcc, useBorderMask, closeRadius, blobOpts, gt, outDir, stem, typing, shotGray);
}
return 0;

static double ParseInv(string s) => double.Parse(s, CultureInfo.InvariantCulture);

// ---- local helpers ----

static double BorderBandMeanDeviation(float[] dev, int w, int h, double frac)
{
    int bx = (int)(w * frac), by = (int)(h * frac);
    double sum = 0; int cnt = 0;
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (x < bx || x >= w - bx || y < by || y >= h - by) { sum += dev[y * w + x]; cnt++; }
    return cnt == 0 ? 0 : sum / cnt;
}

static double InteriorMeanDeviation(float[] dev, int w, int h, double frac)
{
    int bx = (int)(w * frac), by = (int)(h * frac);
    double sum = 0; int cnt = 0;
    for (int y = by; y < h - by; y++)
        for (int x = bx; x < w - bx; x++) { sum += dev[y * w + x]; cnt++; }
    return cnt == 0 ? 0 : sum / cnt;
}

// Deviation heatmap: 0 -> black, 1 -> hot (white-ish), via a simple blue->red->white ramp.
static BgraImage Heatmap(float[] dev, int w, int h)
{
    var img = new BgraImage(w, h);
    for (int i = 0; i < dev.Length; i++)
    {
        double t = Math.Clamp(dev[i], 0, 1);
        // ramp: low=dark blue, mid=red, high=yellow/white
        byte r = (byte)(Math.Clamp(t * 1.6, 0, 1) * 255);
        byte g = (byte)(Math.Clamp((t - 0.5) * 2.0, 0, 1) * 255);
        byte b = (byte)(Math.Clamp((0.4 - t) * 2.0, 0, 1) * 255);
        int o = i * 4;
        img.Pixels[o] = b; img.Pixels[o + 1] = g; img.Pixels[o + 2] = r; img.Pixels[o + 3] = 255;
    }
    return img;
}

// Overlay: screenshot, with ncc<=lowNcc pixels tinted red.
static BgraImage Overlay(BgraImage shot, float[] dev, double lowNcc)
{
    var img = new BgraImage(shot.Width, shot.Height);
    Array.Copy(shot.Pixels, img.Pixels, shot.Pixels.Length);
    double thr = 1.0 - lowNcc;
    for (int i = 0; i < dev.Length; i++)
    {
        if (dev[i] >= thr)
        {
            int o = i * 4;
            img.Pixels[o] = (byte)(img.Pixels[o] * 0.3);       // B down
            img.Pixels[o + 1] = (byte)(img.Pixels[o + 1] * 0.3); // G down
            img.Pixels[o + 2] = 255;                              // R up
        }
        img.Pixels[i * 4 + 3] = 255;
    }
    return img;
}

// Minimal BGRA image (the shared lib exposes GrayImage + raw BGRA tuples, not a
// BGRA wrapper); kept local to this throwaway probe.
sealed class BgraImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; } // BGRA, row-major

    public BgraImage(int width, int height)
    {
        Width = width; Height = height; Pixels = new byte[width * height * 4];
    }

    public BgraImage(int width, int height, byte[] pixels)
    {
        Width = width; Height = height; Pixels = pixels;
    }

    public int Index(int x, int y) => (y * Width + x) * 4;
}

static class Cli
{
    public static string Get(string[] a, string key, string def)
    {
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i] == key) return a[i + 1];
        return def;
    }

    public static bool Has(string[] a, string key) => Array.IndexOf(a, key) >= 0;
}

static class Gray
{
    public static float[] ToFloat(BgraImage img)
    {
        var g = new float[img.Width * img.Height];
        for (int i = 0; i < g.Length; i++)
        {
            int o = i * 4;
            g[i] = 0.114f * img.Pixels[o] + 0.587f * img.Pixels[o + 1] + 0.299f * img.Pixels[o + 2];
        }
        return g;
    }

    public static double MeanLuma(BgraImage img)
    {
        double s = 0; int n = img.Width * img.Height;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            s += 0.114 * img.Pixels[o] + 0.587 * img.Pixels[o + 1] + 0.299 * img.Pixels[o + 2];
        }
        return s / n;
    }

    public static Bitmap ToGrayBitmap(BgraImage img)
    {
        var bmp = new Bitmap(img.Width, img.Height);
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                int o = img.Index(x, y);
                int v = (int)(0.114 * img.Pixels[o] + 0.587 * img.Pixels[o + 1] + 0.299 * img.Pixels[o + 2]);
                bmp.SetPixel(x, y, Color.FromArgb(v, v, v));
            }
        return bmp;
    }

    public static float[] FromGrayBitmap(Bitmap bmp)
    {
        var g = new float[bmp.Width * bmp.Height];
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                g[y * bmp.Width + x] = bmp.GetPixel(x, y).R;
        return g;
    }
}

// Per-pixel local NCC via integral images (O(WH), independent of window size).
static class LocalNcc
{
    public static float[] DeviationMap(float[] a, float[] b, int w, int h, int win, out double meanNcc)
    {
        int r = win / 2;
        double[] ia = Integral(a, w, h);
        double[] ib = Integral(b, w, h);
        double[] iaa = IntegralOf(a, a, w, h);
        double[] ibb = IntegralOf(b, b, w, h);
        double[] iab = IntegralOf(a, b, w, h);

        const double flatVar = 3.0;   // below this variance a window has no structure
        const double eps = 1e-6;
        var dev = new float[w * h];
        double nccSum = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), y0 = Math.Max(0, y - r);
                int x1 = Math.Min(w - 1, x + r), y1 = Math.Min(h - 1, y + r);
                double n = (x1 - x0 + 1) * (double)(y1 - y0 + 1);
                double sa = Box(ia, w, x0, y0, x1, y1);
                double sb = Box(ib, w, x0, y0, x1, y1);
                double saa = Box(iaa, w, x0, y0, x1, y1);
                double sbb = Box(ibb, w, x0, y0, x1, y1);
                double sab = Box(iab, w, x0, y0, x1, y1);
                double ma = sa / n, mb = sb / n;
                double va = saa / n - ma * ma;
                double vb = sbb / n - mb * mb;
                double cov = sab / n - ma * mb;

                double ncc;
                if (va < flatVar && vb < flatVar) ncc = 1.0;               // both featureless -> terrain match
                else if (va < flatVar || vb < flatVar) ncc = 0.0;          // structure on one side only -> added content
                else ncc = cov / Math.Sqrt(va * vb + eps);

                ncc = Math.Clamp(ncc, -1, 1);
                nccSum += ncc;
                dev[y * w + x] = (float)Math.Clamp(1.0 - ncc, 0, 1);
            }
        meanNcc = nccSum / (w * h);
        return dev;
    }

    static double[] Integral(float[] src, int w, int h)
    {
        var ii = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double rowSum = 0;
            for (int x = 0; x < w; x++)
            {
                rowSum += src[y * w + x];
                ii[(y + 1) * (w + 1) + (x + 1)] = ii[y * (w + 1) + (x + 1)] + rowSum;
            }
        }
        return ii;
    }

    static double[] IntegralOf(float[] a, float[] b, int w, int h)
    {
        var ii = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double rowSum = 0;
            for (int x = 0; x < w; x++)
            {
                rowSum += (double)a[y * w + x] * b[y * w + x];
                ii[(y + 1) * (w + 1) + (x + 1)] = ii[y * (w + 1) + (x + 1)] + rowSum;
            }
        }
        return ii;
    }

    // Inclusive box sum [x0..x1]x[y0..y1] from an integral image of width (w+1).
    static double Box(double[] ii, int w, int x0, int y0, int x1, int y1)
    {
        int W = w + 1;
        return ii[(y1 + 1) * W + (x1 + 1)] - ii[y0 * W + (x1 + 1)] - ii[(y1 + 1) * W + x0] + ii[y0 * W + x0];
    }
}
