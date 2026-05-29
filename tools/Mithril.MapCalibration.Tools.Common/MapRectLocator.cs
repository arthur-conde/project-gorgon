namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Finds where the area map's rendered window sits inside a screenshot.
/// <see cref="MapRect"/> is the visible map's bounding box in screenshot
/// pixels; combined with the texture's known dimensions it gives the
/// screenshot↔texture transform under the v1 assumption (player has zoomed
/// the in-game map all the way out so the whole texture fits in view, pan = 0).
///
/// <para>Auto-detect attempts NCC of the downsampled area map against the
/// downsampled screenshot at several scales; if the best match's confidence is
/// below the threshold, the caller is expected to fall back to a user-supplied
/// <c>--map-rect</c>. Zoomed-in screenshots (where the visible map is only a
/// pan window) defeat this v1 approach — the user must zoom out first.</para>
/// </summary>
public static class MapRectLocator
{
    /// <summary>
    /// Tries NCC across a coarse scale ladder. Returns null on weak/no match.
    /// Scales chosen from typical PG map UI sizes — the rendered map fills
    /// 30%-80% of a 1080p screenshot's smaller dimension at the zoom levels
    /// most people screenshot at.
    /// </summary>
    public static MapRect? AutoDetect(GrayImage screenshot, GrayImage texture, double minScore)
    {
        // Candidate downsample factors for the texture — each gives a different
        // "rendered map size" hypothesis. The match's score peak picks the
        // closest hypothesis. Fractional factors are essential when the user
        // zooms the in-game map all the way out so the entire texture fits
        // exactly in the visible window — the right factor is ~texture/screen
        // and integer-only resampling skips past it.
        var candidates = BuildCandidateScales(screenshot, texture);
        if (candidates.Count == 0) return null;

        MapRect? bestRect = null;
        double bestScore = double.NegativeInfinity;
        Console.WriteLine($"[locate] trying {candidates.Count} scales:");
        foreach (var (factor, downsampledTexture) in candidates)
        {
            var hit = NccTemplateMatch.FindBest(screenshot, downsampledTexture, templateMask: null, minScore: -1.0);
            if (hit is null)
            {
                Console.WriteLine($"        f={factor:0.00}  template={downsampledTexture.Width}x{downsampledTexture.Height}  no NCC position");
                continue;
            }
            Console.WriteLine($"        f={factor:0.00}  template={downsampledTexture.Width}x{downsampledTexture.Height}  best=({hit.Value.X},{hit.Value.Y})  score={hit.Value.Score:0.000}");
            if (hit.Value.Score > bestScore)
            {
                bestScore = hit.Value.Score;
                bestRect = new MapRect(
                    OriginX: hit.Value.X,
                    OriginY: hit.Value.Y,
                    Width: downsampledTexture.Width,
                    Height: downsampledTexture.Height,
                    TextureWidth: texture.Width,
                    TextureHeight: texture.Height,
                    AutoDetectScore: hit.Value.Score,
                    SourceScaleFactor: factor);
            }
        }
        return (bestRect is not null && bestScore >= minScore) ? bestRect : null;
    }

    private static List<(double Factor, GrayImage Downsampled)> BuildCandidateScales(
        GrayImage screenshot, GrayImage texture)
    {
        var result = new List<(double, GrayImage)>();
        int sw = screenshot.Width;
        int sh = screenshot.Height;

        // Fractional factors over the full range of plausible map-renders:
        //   - factor ≈ texture/screenshot when the in-game map fills the whole
        //     screenshot (the "zoomed all the way out" case — common, and where
        //     integer-only factors badly bracket the right scale)
        //   - factor in 3..6 when the map UI panel is a smaller region
        // Each candidate is bilinearly resampled so we can test factors like 2.1
        // that integer downsampling can't represent.
        var factors = new double[]
        {
            // Down to 1.0 to cover the "screenshot already at native scale"
            // case (e.g., the synthetic test, or any caller that pre-downsamples
            // both inputs to the same coarse resolution).
            1.0, 1.1, 1.2, 1.35, 1.5, 1.75, 2.0, 2.1, 2.2, 2.4, 2.6, 2.8,
            3.0, 3.25, 3.5, 4.0, 4.5, 5.0, 6.0, 8.0, 10.0,
        };
        foreach (var f in factors)
        {
            int dw = (int)Math.Round(texture.Width / f);
            int dh = (int)Math.Round(texture.Height / f);
            // Template must fit inside the search image; below 25% of screen
            // is unlikely to be a real map render and is dominated by noise.
            if (dw > sw || dh > sh) continue;
            int dsmaller = Math.Min(dw, dh);
            int smaller = Math.Min(sw, sh);
            if (dsmaller < smaller * 0.25) continue;
            result.Add((f, ImageIo.Resize(texture, dw, dh)));
        }
        return result;
    }
}

/// <summary>
/// Visible map's bounding box in the screenshot, plus the source texture's
/// native dimensions. Combined these give the screenshot↔texture transform.
/// </summary>
public sealed record MapRect(
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    int TextureWidth,
    int TextureHeight,
    double? AutoDetectScore = null,
    double? SourceScaleFactor = null)
{
    public (double Tx, double Ty) ScreenshotToTexture(double sx, double sy)
    {
        var scaleX = (double)TextureWidth / Width;
        var scaleY = (double)TextureHeight / Height;
        return ((sx - OriginX) * scaleX, (sy - OriginY) * scaleY);
    }
}
