namespace StickyMD.App.Interop;

/// <summary>
/// Rounded window corners via DWM.
/// </summary>
/// <remarks>
/// This is the ONLY route to rounded corners here. Per-pixel transparency is
/// unavailable: the WebView2 backdrop must be opaque, because with alpha 0 the
/// composition surface composites against black rather than against the WPF
/// layer beneath it. So the WPF layer cannot show through the note content, and
/// a clipped-geometry approach would round the chrome while leaving the note
/// body square. Verified in docs/spikes/2026-08-22-spike-0-transparency.md.
/// </remarks>
public static class DwmCorners
{
    public static void Round(IntPtr hwnd)
    {
        var preference = NativeMethods.DWMWCP_ROUND;

        // Ignore the HRESULT. On a build without corner preferences this
        // returns a failure and the window is square -- cosmetic, and not
        // worth failing to open a note over.
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref preference,
            sizeof(int));
    }
}
