namespace StickyMD.Core.Geometry;

/// <summary>
/// A rectangle in PHYSICAL screen pixels. Never device-independent units --
/// see the spec section "Geometry" for why.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// Plain data describing one monitor. Produced by the App layer's monitor
/// enumeration; Core never queries the OS for this.
/// </summary>
public sealed record MonitorInfo(
    PixelRect Bounds,
    PixelRect WorkArea,
    double Dpi,
    bool IsPrimary,
    string DeviceName);

public static class WindowPlacement
{
    /// <summary>
    /// Returns a rect guaranteed to sit inside one of the supplied monitors'
    /// work areas. A rect already fully visible is returned untouched.
    /// </summary>
    public static PixelRect Clamp(PixelRect saved, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return saved;

        foreach (var monitor in monitors)
            if (Contains(monitor.WorkArea, saved)) return saved;

        var target = PickTarget(saved, monitors);
        return Fit(saved, target.WorkArea);
    }

    private static MonitorInfo PickTarget(
        PixelRect saved, IReadOnlyList<MonitorInfo> monitors)
    {
        MonitorInfo? best = null;
        var bestArea = 0L;

        foreach (var monitor in monitors)
        {
            var area = IntersectionArea(monitor.WorkArea, saved);
            if (area > bestArea)
            {
                bestArea = area;
                best = monitor;
            }
        }

        return best
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors[0];
    }

    private static PixelRect Fit(PixelRect rect, PixelRect work)
    {
        var width = Math.Min(rect.Width, work.Width);
        var height = Math.Min(rect.Height, work.Height);

        var x = Math.Clamp(rect.X, work.X, work.Right - width);
        var y = Math.Clamp(rect.Y, work.Y, work.Bottom - height);

        return new PixelRect(x, y, width, height);
    }

    private static bool Contains(PixelRect outer, PixelRect inner)
        => inner.X >= outer.X
        && inner.Y >= outer.Y
        && inner.Right <= outer.Right
        && inner.Bottom <= outer.Bottom;

    private static long IntersectionArea(PixelRect a, PixelRect b)
    {
        var width = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return width <= 0 || height <= 0 ? 0 : (long)width * height;
    }
}
