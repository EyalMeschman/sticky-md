using StickyMD.Core.Geometry;

namespace StickyMD.App.Interop;

/// <summary>
/// Monitor discovery. Injected so <c>WindowManager</c> can be tested against
/// synthetic monitor sets -- unplugging a display in a unit test is otherwise
/// not an option.
/// </summary>
public interface IMonitorProvider
{
    IReadOnlyList<MonitorInfo> GetMonitors();
}

/// <summary>
/// One monitor exactly as Win32 reported it, before any interpretation.
/// </summary>
/// <param name="DpiX">
/// Raw effective DPI, or 0 when <c>GetDpiForMonitor</c> failed. Recorded as 0
/// rather than guessed, so the mapping decides in one place.
/// </param>
public sealed record RawMonitor(
    PixelRect Bounds,
    PixelRect WorkArea,
    uint DpiX,
    bool IsPrimary,
    string DeviceName);

/// <summary>
/// Win32 monitor discovery feeding <see cref="WindowPlacement.Clamp"/>.
/// Discovery lives in App; the placement math lives in Core.
/// </summary>
public sealed class MonitorEnumerator : IMonitorProvider
{
    /// <summary>
    /// The interpretation half, separated from the P/Invoke half so it is
    /// testable. This is where the bugs are: the DPI convention, the ordering
    /// WindowPlacement's fallbacks assume, and the zero-value guards.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> Map(IReadOnlyList<RawMonitor> raw)
    {
        var mapped = new List<MonitorInfo>(raw.Count);

        foreach (var monitor in raw)
        {
            mapped.Add(new MonitorInfo(
                monitor.Bounds,
                monitor.WorkArea,
                // RAW DPI -- 96, 120, 144 -- never a scale factor. Plan A's
                // WindowPlacementTests construct Dpi: 96 and Dpi: 144, and a
                // scale factor here would be 96x off with nothing failing
                // loudly. Zero means GetDpiForMonitor failed; 96 is the only
                // safe answer, and a Dpi of 0 downstream is a division waiting
                // to happen.
                monitor.DpiX == 0 ? 96 : monitor.DpiX,
                monitor.IsPrimary,
                // notes.json stores this. An empty string would be
                // indistinguishable from "not recorded", and the field exists
                // from v1 so prefer-original-monitor can land later.
                string.IsNullOrWhiteSpace(monitor.DeviceName)
                    ? "(unknown)"
                    : monitor.DeviceName));
        }

        // Primary first. WindowPlacement.PickTarget falls back to
        // FirstOrDefault(IsPrimary) and then to monitors[0]; ordering primary
        // first makes those two fallbacks agree rather than quietly disagree
        // depending on enumeration order.
        //
        // OrderByDescending, NOT List.Sort -- which is what made the
        // "deterministic" claim above false. Array.Sort special-cases a
        // three-element range with three SwapIfGreater calls, and that
        // sequence is not stable: [DISPLAY2, DISPLAY3, DISPLAY1-primary] came
        // out as [DISPLAY1, DISPLAY3, DISPLAY2], reversing the two
        // non-primaries. notes.json records the device name, so a
        // run-to-run reshuffle makes a recorded monitor mean nothing. LINQ's
        // OrderBy is documented stable, so non-primary monitors keep
        // EnumDisplayMonitors' order. Verified by
        // MonitorMappingTests.The_non_primary_monitors_keep_their_enumeration_order,
        // which fails if List.Sort comes back.
        return mapped.OrderByDescending(m => m.IsPrimary).ToList();
    }

    public IReadOnlyList<MonitorInfo> GetMonitors() => Map(Enumerate());

    private static List<RawMonitor> Enumerate()
    {
        var raw = new List<RawMonitor>();

        // The callback is held in a local so the GC cannot collect the
        // delegate while native code is still calling it. Passing a lambda
        // directly is the classic crash here, and it only reproduces under
        // memory pressure.
        NativeMethods.MonitorEnumProc callback =
            (IntPtr hMonitor, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            var info = new NativeMethods.MONITORINFOEXW
            {
                cbSize = System.Runtime.InteropServices.Marshal
                    .SizeOf<NativeMethods.MONITORINFOEXW>(),
            };

            if (!NativeMethods.GetMonitorInfoW(hMonitor, ref info)) return true;

            uint dpi = 0;
            if (NativeMethods.GetDpiForMonitor(
                    hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0)
            {
                dpi = dpiX;
            }

            raw.Add(new RawMonitor(
                ToRect(info.rcMonitor),
                ToRect(info.rcWork),
                dpi,
                (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                info.szDevice ?? string.Empty));

            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        return raw;
    }

    private static PixelRect ToRect(NativeMethods.RECT r)
        => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
