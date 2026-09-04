using StickyMD.Core.Geometry;

namespace StickyMD.App.Interop;

/// <summary>
/// Reads and writes a window's rectangle in PHYSICAL screen pixels.
/// </summary>
/// <remarks>
/// WPF's Left/Top/Width/Height are device-independent units scaled by the
/// PRIMARY monitor's DPI, so saving a note on a 150% display and restoring it
/// on a 100% one puts the window in the wrong place -- the exact failure
/// success criterion 3 rules out. GetWindowRect and SetWindowPos speak physical
/// pixels on a PerMonitorV2 process, and the index stores physical pixels, so
/// there is no conversion anywhere and therefore no conversion to get wrong.
/// </remarks>
public static class WindowGeometry
{
    public static PixelRect GetBounds(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r))
            return new PixelRect(0, 0, 0, 0);

        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static void SetBounds(IntPtr hwnd, PixelRect rect)
    {
        // NOACTIVATE so restoring a screenful of notes at startup does not
        // fight the logon sequence for focus, and NOZORDER so it does not
        // undo Topmost.
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero,
            rect.X, rect.Y, rect.Width, rect.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }
}
