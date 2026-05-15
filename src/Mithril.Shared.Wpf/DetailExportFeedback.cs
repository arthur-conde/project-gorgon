using System.Windows.Controls;
using System.Windows.Threading;
using MahApps.Metro.IconPacks;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Transient visual ack for the detail-window "Copy as image" button. There is no
/// shared toast infrastructure, so the button itself is the affordance: its camera
/// glyph briefly swaps to a check (success) or alert (failure) then restores.
/// </summary>
public static class DetailExportFeedback
{
    private static readonly TimeSpan HoldFor = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// Flashes the result on <paramref name="button"/> (whose content is the
    /// <see cref="PackIconLucide"/> camera glyph), then restores the original icon.
    /// </summary>
    public static void Run(bool success, Button button)
    {
        if (button.Content is not PackIconLucide icon)
            return;

        var original = icon.Kind;
        icon.Kind = success ? PackIconLucideKind.Check : PackIconLucideKind.TriangleAlert;

        var timer = new DispatcherTimer { Interval = HoldFor };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            icon.Kind = original;
        };
        timer.Start();
    }
}
