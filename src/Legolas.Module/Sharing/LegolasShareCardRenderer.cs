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

    /// <summary>1000×400 social card. Async — renders on a background STA worker thread
    /// so the UI stays responsive while WPF lays out the visual tree.</summary>
    public Task<BitmapSource> RenderCardAsync(LegolasSharePayload payload)
        => RenderOnStaWorker(() =>
        {
            var vm = new LegolasShareCardViewModel(payload, _refData, _iconCache);
            var view = new LegolasShareCardView { DataContext = vm };
            return RenderControl(view, LegolasShareCardViewModel.CardWidth, LegolasShareCardViewModel.CardHeight);
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

    private static BitmapSource RenderControl(FrameworkElement control, double width, double height)
    {
        // Force a layout pass so every binding resolves before the render. Without the
        // explicit Measure/Arrange/UpdateLayout dance, a never-parented FrameworkElement
        // has zero size and RenderTargetBitmap captures nothing.
        control.Width = width;
        control.Height = height;
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width),
            (int)Math.Ceiling(height),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        bitmap.Freeze();
        return bitmap;
    }
}
