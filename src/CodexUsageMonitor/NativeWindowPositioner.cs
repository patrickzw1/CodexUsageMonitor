using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CodexUsageMonitor.Core.Services;
using Forms = System.Windows.Forms;

namespace CodexUsageMonitor;

internal static class NativeWindowPositioner
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public static void PlaceMainWindow(Window window, Forms.Screen screen)
    {
        window.Show();
        window.UpdateLayout();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
        {
            return;
        }

        var area = screen.WorkingArea;
        var width = Math.Min(rect.Right - rect.Left, Math.Max(1, area.Width - 16));
        var height = Math.Min(rect.Bottom - rect.Top, Math.Max(1, area.Height - 20));
        SetWindowPos(
            handle,
            IntPtr.Zero,
            area.Right - width - 12,
            area.Bottom - height - 10,
            width,
            height,
            SwpNoZOrder | SwpNoActivate);
    }

    public static void PlaceTrayMenu(Window window, Forms.Screen screen, System.Drawing.Point cursor)
    {
        window.Show();
        window.UpdateLayout();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
        {
            return;
        }

        var area = screen.WorkingArea;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var (left, top) = TrayWindowPlacement.Calculate(area, cursor, width, height);
        SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, SwpNoZOrder | SwpNoActivate | 0x0001);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
