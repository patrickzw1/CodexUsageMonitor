using System.Drawing;

namespace CodexUsageMonitor.Core.Services;

public static class TrayWindowPlacement
{
    public static (int Left, int Top) Calculate(Rectangle workingArea, Point cursor, int width, int height)
    {
        var minimumLeft = workingArea.Left + 8;
        var maximumLeft = Math.Max(minimumLeft, workingArea.Right - width - 8);
        var minimumTop = workingArea.Top + 8;
        var maximumTop = Math.Max(minimumTop, workingArea.Bottom - height - 8);
        return (
            Math.Clamp(cursor.X - width + 18, minimumLeft, maximumLeft),
            Math.Clamp(cursor.Y - height - 8, minimumTop, maximumTop));
    }
}
