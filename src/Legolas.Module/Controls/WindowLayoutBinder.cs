using System.Windows;
using Legolas.Domain;

namespace Legolas.Controls;

public static class WindowLayoutBinder
{
    public static void Bind(Window window, WindowLayout layout, Action? onChanged = null)
    {
        Apply(window, layout);

        window.LocationChanged += (_, _) =>
        {
            if (window.WindowState != WindowState.Normal) return;
            layout.Left = window.Left;
            layout.Top = window.Top;
            onChanged?.Invoke();
        };
        window.SizeChanged += (_, _) =>
        {
            if (window.WindowState != WindowState.Normal) return;
            layout.Width = window.Width;
            layout.Height = window.Height;
            onChanged?.Invoke();
        };
    }

    private static void Apply(Window window, WindowLayout layout)
    {
        if (!double.IsNaN(layout.Left)) window.Left = layout.Left;
        if (!double.IsNaN(layout.Top)) window.Top = layout.Top;
        if (layout.Width > 0) window.Width = layout.Width;
        if (layout.Height > 0) window.Height = layout.Height;
    }
}
