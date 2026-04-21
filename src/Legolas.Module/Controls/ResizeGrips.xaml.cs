using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Legolas.Controls;

public partial class ResizeGrips : UserControl
{
    private const double MinWidth_ = 160;
    private const double MinHeight_ = 100;

    public ResizeGrips()
    {
        InitializeComponent();
    }

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb thumb) return;
        if (Window.GetWindow(this) is not Window window) return;
        var dir = thumb.Tag as string;
        if (string.IsNullOrEmpty(dir)) return;

        var left = window.Left;
        var top = window.Top;
        var width = window.Width;
        var height = window.Height;
        var dx = e.HorizontalChange;
        var dy = e.VerticalChange;

        if (dir.Contains('W'))
        {
            var newW = Math.Max(MinWidth_, width - dx);
            window.Left = left + (width - newW);
            window.Width = newW;
        }
        else if (dir.Contains('E'))
        {
            window.Width = Math.Max(MinWidth_, width + dx);
        }

        if (dir.Contains('N'))
        {
            var newH = Math.Max(MinHeight_, height - dy);
            window.Top = top + (height - newH);
            window.Height = newH;
        }
        else if (dir.Contains('S'))
        {
            window.Height = Math.Max(MinHeight_, height + dy);
        }
    }
}
