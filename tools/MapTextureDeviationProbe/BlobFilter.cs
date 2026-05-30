using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;

// =====================================================================
// SHAPE/SIZE FILTER — turns the "all added content" deviation map into a
// clean icon-candidate set (mithril#897 remaining-work item 1).
//
// Pipeline: threshold deviation -> [border-mask] -> [morphological close] ->
// connected components -> per-blob features -> classify icon/fog/structure.
// Icons render ~16 px and, with the local-NCC window smearing the boundary by
// +-(window/2), produce compact high-peak blobs roughly 12-30 px across. Fog-of-
// war is large, soft, low-gradient. Structures (the Serbule keep, labels) are
// large and elongated/high-contrast. Size is the primary separator; solidity +
// aspect + peak-deviation reject labels, fragmented terrain noise, and soft fog.
//
// Types live here (not in Program.cs) so the top-level statements stay readable;
// they share the global namespace, so BgraImage / Cli from Program.cs are visible.
// =====================================================================

internal enum BlobClass { Noise, Icon, Fog, Structure }

internal readonly record struct BlobOptions(
    int MinArea, int MaxIconArea, double MinSolidity, double MaxAspect, double MinPeak);

internal sealed record GroundTruthInputs(
    string Area, string LandmarksPath, string NpcsPath, string BaselinePath,
    double Tol, int TexW, int TexH);

/// <summary>Per-blob geometry + deviation stats. Pixel list retained for rendering the fill.</summary>
internal sealed class BlobFeat
{
    public List<int> Pixels { get; } = new();
    public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue;
    public double SumX, SumY, SumDev, PeakDev;
    public int Area => Pixels.Count;
    public int W => MaxX - MinX + 1;
    public int H => MaxY - MinY + 1;
    public double Cx => SumX / Area;
    public double Cy => SumY / Area;
    public double MeanDev => SumDev / Area;
    public double Solidity => (double)Area / Math.Max(1, W * H);
    public double Aspect => (double)Math.Max(W, H) / Math.Max(1, Math.Min(W, H));
}

internal static class BlobStage
{
    public static void Run(BgraImage shot, float[] dev, double lowNcc, bool useBorderMask,
        int closeRadius, BlobOptions opts, GroundTruthInputs? gt, string outDir, string stem)
    {
        int w = shot.Width, h = shot.Height, n = w * h;
        double devThr = 1.0 - lowNcc;  // dev >= devThr  <=>  ncc <= lowNcc

        var fg = new bool[n];
        int fgCount = 0;
        for (int i = 0; i < n; i++) if (dev[i] >= devThr) { fg[i] = true; fgCount++; }
        Console.WriteLine();
        Console.WriteLine($"[blobs] threshold ncc<={lowNcc:0.00} (dev>={devThr:0.00}) -> {fgCount} fg px ({(double)fgCount / n:P1})");

        if (useBorderMask)
        {
            var border = BorderMask.Compute(shot.Pixels, w, h, 4);
            int dropped = 0;
            for (int i = 0; i < n; i++) if (fg[i] && border[i]) { fg[i] = false; dropped++; }
            Console.WriteLine($"[blobs] border-mask cleared {dropped} fg px ({(double)dropped / Math.Max(1, fgCount):P0} of fg)");
        }

        if (closeRadius > 0)
        {
            fg = Morphology.Close(fg, w, h, closeRadius);
            Console.WriteLine($"[blobs] morphological close r={closeRadius}");
        }

        var comps = ConnectedComponents.Label(fg, w, h, dev);
        Console.WriteLine($"[blobs] {comps.Count} connected components");

        var classified = new List<(BlobFeat F, BlobClass C)>(comps.Count);
        foreach (var f in comps) classified.Add((f, Classify(f, opts)));

        var icons = classified.Where(b => b.C == BlobClass.Icon).Select(b => b.F).ToList();
        int fogN = classified.Count(b => b.C == BlobClass.Fog);
        int structN = classified.Count(b => b.C == BlobClass.Structure);
        int noiseN = classified.Count(b => b.C == BlobClass.Noise);
        Console.WriteLine($"[blobs] classified: {icons.Count} ICON, {fogN} fog, {structN} structure, {noiseN} noise/rejected");
        if (icons.Count > 0)
            Console.WriteLine($"[blobs] icon-candidate area px: min={icons.Min(f => f.Area)} median={Median(icons.Select(f => (double)f.Area)):0} max={icons.Max(f => f.Area)}");

        // Project ground truth (Serbule only) before rendering so the overlay can mark it.
        List<(double X, double Y, string Name)> gtPx = gt is null ? new() : ProjectGroundTruth(gt, w, h);

        var overlay = Render(shot, classified, gtPx);
        string path = Path.Combine(outDir, $"{stem}_blobs.png");
        ImageIo.SaveBgraPng(overlay.Pixels, w, h, path);
        Console.WriteLine($"[blobs] wrote {path}  (green=icon, blue=fog, red=structure" + (gt != null ? ", yellow x=ground-truth ref)" : ")"));

        if (gt != null) EvaluateGroundTruth(classified, icons, gtPx, gt.Tol, w, h);
    }

    private static BlobClass Classify(BlobFeat f, BlobOptions o)
    {
        if (f.Area < o.MinArea) return BlobClass.Noise;
        bool iconBand = f.Area <= o.MaxIconArea;
        if (iconBand && f.Solidity >= o.MinSolidity && f.Aspect <= o.MaxAspect && f.PeakDev >= o.MinPeak)
            return BlobClass.Icon;
        if (f.Area > o.MaxIconArea)
        {
            // Large blobs are all rejected; the split is for the visualization
            // only. Structures (keep, labels) are elongated or sharply deviating;
            // fog-of-war is a large, soft, low-gradient region.
            if (f.Aspect >= 2.2 || f.MeanDev >= 0.6) return BlobClass.Structure;
            return BlobClass.Fog;
        }
        return BlobClass.Noise;  // icon-sized but failed a shape gate
    }

    private static List<(double X, double Y, string Name)> ProjectGroundTruth(GroundTruthInputs gt, int shotW, int shotH)
    {
        var cal = BaselineFile.TryReadAnchor(gt.BaselinePath, gt.Area)
            ?? throw new InvalidOperationException(
                $"--ground-truth: no committed baseline for area '{gt.Area}' in {gt.BaselinePath}");
        var refs = new List<LandmarkRef>();
        refs.AddRange(LandmarksReader.LoadForArea(gt.LandmarksPath, gt.Area));
        refs.AddRange(NpcsReader.LoadForArea(gt.NpcsPath, gt.Area));

        // WorldToWindow yields TEXTURE-pixel coords (the baseline was solved in
        // texture space); scale to screenshot pixels by the same extent ratio the
        // proven calibrator uses (mapRect.Width / TextureWidth) — here the full
        // screenshot vs the loaded texture dims.
        var pts = new List<(double, double, string)>();
        int onScreen = 0;
        foreach (var r in refs)
        {
            var pred = cal.WorldToWindow(new WorldCoord(r.World.X, 0, r.World.Z));
            double sx = pred.X * shotW / gt.TexW;
            double sy = pred.Y * shotH / gt.TexH;
            if (sx < 0 || sx >= shotW || sy < 0 || sy >= shotH) continue;
            pts.Add((sx, sy, r.Name));
            onScreen++;
        }
        Console.WriteLine($"[gt] {refs.Count} refs ({gt.Area}); {onScreen} project on-screen via committed baseline");
        return pts;
    }

    private static void EvaluateGroundTruth(List<(BlobFeat F, BlobClass C)> classified,
        List<BlobFeat> icons, List<(double X, double Y, string Name)> gtPx, double tol, int w, int h)
    {
        if (gtPx.Count == 0) { Console.WriteLine("[gt] no on-screen refs to score"); return; }

        // Pixel set of all rejected LARGE blobs (structure/fog) so we can tell
        // how many refs project ONTO the keep/structure — those can never form
        // a separate icon blob, so they cap achievable recall structurally.
        var largePx = new HashSet<int>();
        foreach (var (f, c) in classified)
            if (c is BlobClass.Structure or BlobClass.Fog)
                foreach (var p in f.Pixels) largePx.Add(p);
        bool UnderLarge((double X, double Y, string Name) g)
        {
            int x = (int)Math.Round(g.X), y = (int)Math.Round(g.Y);
            return x >= 0 && x < w && y >= 0 && y < h && largePx.Contains(y * w + x);
        }

        int gtHit = gtPx.Count(g => icons.Any(f => Dist(f.Cx, f.Cy, g.X, g.Y) <= tol));
        int candHit = icons.Count(f => gtPx.Any(g => Dist(f.Cx, f.Cy, g.X, g.Y) <= tol));
        var separable = gtPx.Where(g => !UnderLarge(g)).ToList();
        int sepHit = separable.Count(g => icons.Any(f => Dist(f.Cx, f.Cy, g.X, g.Y) <= tol));
        int underLarge = gtPx.Count - separable.Count;

        Console.WriteLine($"[gt] recall    {gtHit}/{gtPx.Count} ({(double)gtHit / gtPx.Count:P0}) refs have an icon candidate within {tol:0}px");
        Console.WriteLine($"[gt]   of which {underLarge} refs project ONTO a rejected structure/fog blob (e.g. the central keep) — structurally unseparable");
        Console.WriteLine($"[gt]   recall on the {separable.Count} separable refs: {sepHit}/{separable.Count} ({(separable.Count == 0 ? 0 : (double)sepHit / separable.Count):P0})");
        Console.WriteLine($"[gt] precision {candHit}/{icons.Count} ({(icons.Count == 0 ? 0 : (double)candHit / icons.Count):P0}) icon candidates land within {tol:0}px of a ref");
        Console.WriteLine("[gt] NOTE recall is still a lower bound: GT = ALL landmarks+NPCs in area, but not all are rendered (off-map / unshown NPCs).");
    }

    private static BgraImage Render(BgraImage shot, List<(BlobFeat F, BlobClass C)> classified, List<(double X, double Y, string Name)> gtPx)
    {
        var img = new BgraImage(shot.Width, shot.Height);
        Array.Copy(shot.Pixels, img.Pixels, shot.Pixels.Length);
        for (int i = 0; i < img.Pixels.Length; i += 4) img.Pixels[i + 3] = 255;

        foreach (var (f, c) in classified)
        {
            if (c == BlobClass.Noise) continue;  // keep the overlay clean
            (byte r, byte g, byte b) = c switch
            {
                BlobClass.Icon => ((byte)0, (byte)255, (byte)0),
                BlobClass.Fog => ((byte)40, (byte)120, (byte)255),
                _ => ((byte)255, (byte)40, (byte)40),  // Structure
            };
            // Semi-transparent fill.
            foreach (var p in f.Pixels)
            {
                int o = p * 4;
                img.Pixels[o] = (byte)((img.Pixels[o] * 0.55) + b * 0.45);
                img.Pixels[o + 1] = (byte)((img.Pixels[o + 1] * 0.55) + g * 0.45);
                img.Pixels[o + 2] = (byte)((img.Pixels[o + 2] * 0.55) + r * 0.45);
            }
            // Solid bounding box.
            ImageIo.DrawRect(img.Pixels, img.Width, img.Height, f.MinX, f.MinY, f.W, f.H, r, g, b);
        }

        // Ground-truth refs: yellow cross at each projected on-screen position.
        foreach (var (x, y, _) in gtPx)
            ImageIo.DrawCross(img.Pixels, img.Width, img.Height, (int)Math.Round(x), (int)Math.Round(y), 5, 255, 255, 0);

        return img;
    }

    private static double Dist(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx, dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(v => v).ToArray();
        if (a.Length == 0) return 0;
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }
}

/// <summary>
/// 8-connected component labelling over a boolean foreground mask. Iterative
/// stack (no recursion — a single large fog/border component can span 100k+ px).
/// </summary>
internal static class ConnectedComponents
{
    public static List<BlobFeat> Label(bool[] fg, int w, int h, float[] dev)
    {
        var seen = new bool[fg.Length];
        var comps = new List<BlobFeat>();
        var stack = new Stack<int>();
        for (int start = 0; start < fg.Length; start++)
        {
            if (!fg[start] || seen[start]) continue;
            var f = new BlobFeat();
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % w, py = p / w;
                f.Pixels.Add(p);
                f.SumX += px; f.SumY += py; f.SumDev += dev[p];
                if (dev[p] > f.PeakDev) f.PeakDev = dev[p];
                if (px < f.MinX) f.MinX = px;
                if (px > f.MaxX) f.MaxX = px;
                if (py < f.MinY) f.MinY = py;
                if (py > f.MaxY) f.MaxY = py;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = px + dx, ny = py + dy;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        int q = ny * w + nx;
                        if (fg[q] && !seen[q]) { seen[q] = true; stack.Push(q); }
                    }
            }
            comps.Add(f);
        }
        return comps;
    }
}

/// <summary>
/// Square-element morphological close (dilate then erode) to bridge fragmented
/// icon pixels into a single component without growing the overall footprint.
/// </summary>
internal static class Morphology
{
    public static bool[] Close(bool[] src, int w, int h, int r)
    {
        var dil = Dilate(src, w, h, r);
        return Erode(dil, w, h, r);
    }

    private static bool[] Dilate(bool[] s, int w, int h, int r)
    {
        var o = new bool[s.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!s[y * w + x]) continue;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && nx < w && ny >= 0 && ny < h) o[ny * w + nx] = true;
                    }
            }
        return o;
    }

    private static bool[] Erode(bool[] s, int w, int h, int r)
    {
        var o = new bool[s.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool all = true;
                for (int dy = -r; dy <= r && all; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h || !s[ny * w + nx]) { all = false; break; }
                    }
                o[y * w + x] = all;
            }
        return o;
    }
}
