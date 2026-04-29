using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Mithril.Shared.Wpf;

public static partial class WindowFlasher
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FLASHWINFO pwfi);

    public static void Flash(Window window)
    {
        var helper = new WindowInteropHelper(window);
        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = helper.Handle,
            dwFlags = 0x0000000F, // FLASHW_ALL | FLASHW_TIMERNOFG = 3 | 12
            uCount = 5,
            dwTimeout = 0,
        };
        FlashWindowEx(ref fi);
    }
}
