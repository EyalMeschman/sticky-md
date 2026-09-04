using StickyMD.App.Interop;
using StickyMD.Core.Geometry;

namespace StickyMD.App.Tests.TestSupport;

/// <summary>A fixed monitor set. Task 13's tests clamp against these.</summary>
public sealed class FakeMonitorProvider(params MonitorInfo[] monitors) : IMonitorProvider
{
    public IReadOnlyList<MonitorInfo> Monitors { get; set; } = monitors;

    public int CallCount { get; private set; }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        CallCount++;
        return Monitors;
    }

    /// <summary>Two 2560x1440 displays at 100%, matching the dev machine.</summary>
    public static FakeMonitorProvider TwoAt100Percent() => new(
        new MonitorInfo(
            new PixelRect(0, 0, 2560, 1440),
            new PixelRect(0, 0, 2560, 1392),
            96, true, @"\\.\DISPLAY1"),
        new MonitorInfo(
            new PixelRect(2560, 0, 2560, 1440),
            new PixelRect(2560, 0, 2560, 1392),
            96, false, @"\\.\DISPLAY2"));

    /// <summary>A single display, for the "monitor unplugged" path.</summary>
    public static FakeMonitorProvider OnlyPrimary() => new(
        new MonitorInfo(
            new PixelRect(0, 0, 2560, 1440),
            new PixelRect(0, 0, 2560, 1392),
            96, true, @"\\.\DISPLAY1"));
}
