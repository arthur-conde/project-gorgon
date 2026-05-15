using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Snapshots a live, already-laid-out WPF visual to a clipboard image. Used by the
/// Silmarillion detail windows' "Copy as image" button to lift the detail card body
/// (icon + name + stats + footer) out of the app as a shareable PNG.
/// <para>
/// No off-screen STA worker thread (cf. <c>PippinShareCardRenderer</c>) — the target
/// is the live detail <c>UserControl</c>, already arranged on the UI thread, so it is
/// rendered in place.
/// </para>
/// </summary>
public static class VisualImageExporter
{
    /// <summary>The detail-card surface colour — matches the detail-window Border so the
    /// exported image looks like the on-screen card and is never transparent.</summary>
    private static readonly Color CardSurface = Color.FromRgb(0x1A, 0x1A, 0x1A);

    /// <summary>Render scale. 2× keeps text crisp when pasted into Discord/the wiki;
    /// a fixed factor (not live monitor DPI) keeps the result deterministic.</summary>
    private const double Scale = 2.0;

    /// <summary>Uniform breathing room (device-independent px) drawn in the backfill
    /// colour around the card so the content isn't flush against the image edges.</summary>
    private const double Padding = 16.0;

    /// <summary>
    /// Renders <paramref name="target"/> and places the bitmap on the clipboard.
    /// Returns <c>true</c> on success; <c>false</c> if rendering or the clipboard
    /// failed (clipboard access can transiently throw — the caller surfaces this,
    /// the app never crashes).
    /// </summary>
    public static bool CopyToClipboard(FrameworkElement target)
    {
        try
        {
            var bitmap = RenderToBitmap(target, Scale, CardSurface, Padding);
            // copy: true persists the bitmap past Mithril's process lifetime — the
            // user can copy, close Mithril, and still paste in Discord. Established
            // Pippin/Legolas share-card pattern.
            Clipboard.SetDataObject(new DataObject(DataFormats.Bitmap, bitmap), copy: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Renders <paramref name="target"/> at <paramref name="scale"/>× over a solid
    /// <paramref name="background"/> backfill, inset by <paramref name="padding"/>
    /// device-independent px on every side. Factored out from
    /// <see cref="CopyToClipboard"/> for clarity/reuse.
    /// </summary>
    public static BitmapSource RenderToBitmap(
        FrameworkElement target, double scale, Color background, double padding = 0)
    {
        var width = target.ActualWidth;
        var height = target.ActualHeight;

        // Defensive: a live detail view is always already arranged, but if a caller
        // hands us an un-arranged element, force one layout pass so it has a size.
        if (width <= 0 || height <= 0)
        {
            target.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            target.Arrange(new Rect(target.DesiredSize));
            target.UpdateLayout();
            width = target.ActualWidth > 0 ? target.ActualWidth : target.DesiredSize.Width;
            height = target.ActualHeight > 0 ? target.ActualHeight : target.DesiredSize.Height;
        }

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Export target has no renderable size.");

        // Capture the element exactly as arranged: RenderTargetBitmap.Render(target)
        // paints it 1:1 in its own coordinate space (whitespace included, NO stretch).
        // A VisualBrush would Fill — stretching the visual's content bounding box to
        // the destination and distorting sparse cards — so we render directly, the
        // standard "export this element" path (cf. PippinShareCardRenderer).
        var elementBitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * scale),
            (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale,
            PixelFormats.Pbgra32);
        elementBitmap.Render(target);

        // Crop to what is actually drawn. The element is arranged to its (stretched)
        // pane size, but GetDescendantBounds gives the union of its descendants'
        // rendered geometry — the real content box. Cropping to it makes the export
        // tight in BOTH dimensions regardless of how the pane stretched the live view
        // (no layout disturbance), and drops the card's own empty Border padding so
        // the only margin is this method's `padding` — not stacked → not "doubled".
        var bounds = VisualTreeHelper.GetDescendantBounds(target);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            bounds = new Rect(0, 0, width, height);

        var cropX = Math.Clamp((int)Math.Floor(bounds.X * scale), 0, elementBitmap.PixelWidth - 1);
        var cropY = Math.Clamp((int)Math.Floor(bounds.Y * scale), 0, elementBitmap.PixelHeight - 1);
        var cropW = Math.Clamp((int)Math.Ceiling(bounds.Width * scale), 1, elementBitmap.PixelWidth - cropX);
        var cropH = Math.Clamp((int)Math.Ceiling(bounds.Height * scale), 1, elementBitmap.PixelHeight - cropY);
        var content = new CroppedBitmap(elementBitmap, new Int32Rect(cropX, cropY, cropW, cropH));

        if (padding <= 0)
        {
            content.Freeze();
            return content;
        }

        // Compose the cropped content onto a padding-inset, backfill-filled canvas.
        // DrawImage at the same 96·scale DPI keeps pixels 1:1 — no resample, no blur.
        var contentWidth = cropW / scale;
        var contentHeight = cropH / scale;
        var canvasWidth = contentWidth + 2 * padding;
        var canvasHeight = contentHeight + 2 * padding;
        var composed = new DrawingVisual();
        using (var dc = composed.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(background), pen: null,
                new Rect(0, 0, canvasWidth, canvasHeight));
            dc.DrawImage(content, new Rect(padding, padding, contentWidth, contentHeight));
        }

        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(canvasWidth * scale),
            (int)Math.Ceiling(canvasHeight * scale),
            96 * scale, 96 * scale,
            PixelFormats.Pbgra32);
        rtb.Render(composed);
        rtb.Freeze();
        return rtb;
    }
}
