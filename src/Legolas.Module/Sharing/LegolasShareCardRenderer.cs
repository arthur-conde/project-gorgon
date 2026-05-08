using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mithril.Shared.Icons;
using Mithril.Shared.Reference;

namespace Legolas.Sharing;

/// <summary>
/// Renders a <see cref="LegolasSharePayload"/> to a <see cref="BitmapSource"/>
/// off-screen via <see cref="RenderTargetBitmap"/>. Mirrors Pippin's
/// <c>PippinShareCardRenderer</c> — same STA-worker-thread + Measure/Arrange/UpdateLayout
/// choreography, same <c>BitmapSource.Freeze()</c> so the bitmap crosses threads
/// safely back to the UI thread for clipboard / save dialogs.
/// </summary>
public sealed class LegolasShareCardRenderer
{
    private readonly IReferenceDataService _refData;
    private readonly IIconCacheService _iconCache;

    public LegolasShareCardRenderer(IReferenceDataService refData, IIconCacheService iconCache)
    {
        _refData = refData;
        _iconCache = iconCache;
    }

    /// <summary>1000-wide social card, auto-height. The card view sets a MinHeight
    /// floor so sparse runs don't look dinky; many items grow the card downward via
    /// the WrapPanel. Async — renders on a background STA worker so the UI stays
    /// responsive during layout.</summary>
    public Task<BitmapSource> RenderCardAsync(LegolasSharePayload payload)
        => RenderOnStaWorker(() =>
        {
            var vm = new LegolasShareCardViewModel(payload, _refData, _iconCache);
            var view = new LegolasShareCardView { DataContext = vm };
            return RenderControl(view, LegolasShareCardViewModel.CardWidth, height: null);
        });

    private static Task<BitmapSource> RenderOnStaWorker(Func<BitmapSource> render)
    {
        var tcs = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(render()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "LegolasShareRender",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static BitmapSource RenderControl(FrameworkElement control, double width, double? height)
    {
        // Force a layout pass so every binding resolves before the render. Without the
        // explicit Measure/Arrange/UpdateLayout dance, a never-parented FrameworkElement
        // has zero size and RenderTargetBitmap captures nothing.
        control.Width = width;
        double finalHeight;
        if (height is { } fixedHeight)
        {
            control.Height = fixedHeight;
            control.Measure(new Size(width, fixedHeight));
            control.Arrange(new Rect(0, 0, width, fixedHeight));
            control.UpdateLayout();
            finalHeight = fixedHeight;
        }
        else
        {
            // Two-pass measure: WrapPanel + ItemsControl don't fully materialise their
            // children on a single Measure pass when handed infinite height. We need
            // Measure → Arrange → UpdateLayout → Measure so the second Measure sees
            // realised item containers and reports the true DesiredSize.
            control.Measure(new Size(width, double.PositiveInfinity));
            control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height));
            control.UpdateLayout();
            control.Measure(new Size(width, double.PositiveInfinity));
            var natural = control.DesiredSize.Height;
            control.Arrange(new Rect(0, 0, width, natural));
            control.UpdateLayout();
            finalHeight = natural;
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width),
            (int)Math.Ceiling(finalHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        bitmap.Freeze();
        return bitmap;
    }
}
