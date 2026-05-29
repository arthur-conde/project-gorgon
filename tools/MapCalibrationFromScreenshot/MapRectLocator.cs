namespace Mithril.Tools.MapCalibrationFromScreenshot;

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
internal static class MapRectLocator
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
        // closest hypothesis.
        var candidates = BuildCandidateScales(screenshot, texture);
        if (candidates.Count == 0) return null;

        MapRect? bestRect = null;
        double bestScore = double.NegativeInfinity;
        foreach (var (factor, downsampledTexture) in candidates)
        {
            // NCC the downsampled texture (template) over the screenshot (image).
            // Screenshot is large; downsampled texture should be smaller than it.
            if (downsampledTexture.Width >= screenshot.Width || downsampledTexture.Height >= screenshot.Height)
            {
                continue;
            }
            var hit = NccTemplateMatch.FindBest(screenshot, downsampledTexture, templateMask: null, minScore);
            if (hit is null) continue;
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
        return bestRect;
    }

    private static List<(int Factor, GrayImage Downsampled)> BuildCandidateScales(
        GrayImage screenshot, GrayImage texture)
    {
        var result = new List<(int, GrayImage)>();
        // Aim for downsampled textures that span 30%–80% of the screenshot's
        // smaller dimension. PG textures are ~2000 px, screenshots typically
        // 1080 px high — factor of 3..8 covers the common range.
        int smaller = Math.Min(screenshot.Width, screenshot.Height);
        for (int factor = 2; factor <= 12; factor++)
        {
            int dw = texture.Width / factor;
            int dh = texture.Height / factor;
            int dsmaller = Math.Min(dw, dh);
            if (dsmaller < smaller * 0.25 || dsmaller > smaller * 0.95) continue;
            result.Add((factor, ImageIo.Downsample(texture, factor)));
        }
        return result;
    }
}

/// <summary>
/// Visible map's bounding box in the screenshot, plus the source texture's
/// native dimensions. Combined these give the screenshot↔texture transform.
/// </summary>
internal sealed record MapRect(
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    int TextureWidth,
    int TextureHeight,
    double? AutoDetectScore = null,
    int? SourceScaleFactor = null)
{
    public (double Tx, double Ty) ScreenshotToTexture(double sx, double sy)
    {
        var scaleX = (double)TextureWidth / Width;
        var scaleY = (double)TextureHeight / Height;
        return ((sx - OriginX) * scaleX, (sy - OriginY) * scaleY);
    }
}
