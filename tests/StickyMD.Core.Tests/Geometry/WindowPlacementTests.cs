using Shouldly;
using StickyMD.Core.Geometry;

namespace StickyMD.Core.Tests.Geometry;

public class WindowPlacementTests
{
    // Primary 1920x1080 at origin, work area inset by a 40px taskbar.
    private static MonitorInfo Primary => new(
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1040),
        Dpi: 96, IsPrimary: true, DeviceName: @"\\.\DISPLAY1");

    // Secondary sitting to the LEFT of primary, so it has negative coordinates.
    private static MonitorInfo LeftSecondary => new(
        new PixelRect(-1920, 0, 1920, 1080),
        new PixelRect(-1920, 0, 1920, 1040),
        Dpi: 144, IsPrimary: false, DeviceName: @"\\.\DISPLAY2");

    [Fact]
    public void Empty_monitor_list_returns_the_rect_unchanged()
    {
        var saved = new PixelRect(100, 100, 320, 420);

        WindowPlacement.Clamp(saved, []).ShouldBe(saved);
    }

    [Fact]
    public void Rect_fully_inside_a_work_area_is_unchanged()
    {
        var saved = new PixelRect(100, 100, 320, 420);

        WindowPlacement.Clamp(saved, [Primary]).ShouldBe(saved);
    }

    [Fact]
    public void Rect_on_a_negative_coordinate_monitor_is_unchanged()
    {
        var saved = new PixelRect(-1800, 60, 320, 420);

        WindowPlacement.Clamp(saved, [Primary, LeftSecondary]).ShouldBe(saved);
    }

    [Fact]
    public void Rect_overhanging_the_right_edge_is_pulled_back_in()
    {
        var saved = new PixelRect(1800, 100, 320, 420);

        var result = WindowPlacement.Clamp(saved, [Primary]);

        result.ShouldBe(new PixelRect(1600, 100, 320, 420));
        result.Right.ShouldBe(1920);
    }

    [Fact]
    public void Rect_overhanging_the_bottom_respects_the_taskbar_inset()
    {
        var saved = new PixelRect(100, 900, 320, 420);

        var result = WindowPlacement.Clamp(saved, [Primary]);

        result.Bottom.ShouldBe(1040);
        result.Y.ShouldBe(620);
    }

    [Fact]
    public void Rect_with_negative_position_is_pushed_to_the_origin()
    {
        var saved = new PixelRect(-50, -80, 320, 420);

        WindowPlacement.Clamp(saved, [Primary])
            .ShouldBe(new PixelRect(0, 0, 320, 420));
    }

    [Fact]
    public void Rect_on_a_removed_monitor_lands_inside_the_primary()
    {
        // Saved while DISPLAY2 existed at x = -1920. DISPLAY2 is now gone.
        var saved = new PixelRect(-1700, 200, 320, 420);

        WindowPlacement.Clamp(saved, [Primary])
            .ShouldBe(new PixelRect(0, 200, 320, 420));
    }

    [Fact]
    public void Rect_larger_than_the_work_area_is_shrunk_to_fit()
    {
        var saved = new PixelRect(0, 0, 3000, 2000);

        WindowPlacement.Clamp(saved, [Primary])
            .ShouldBe(new PixelRect(0, 0, 1920, 1040));
    }

    [Fact]
    public void Picks_the_monitor_with_the_largest_overlap()
    {
        // Straddles both monitors. Left wins the overlap contest 92,400 px² to
        // 42,000 px², so PickTarget's comparison is actually exercised -- neither
        // monitor fully contains the rect, so Clamp cannot short-circuit.
        var saved = new PixelRect(-220, 100, 320, 420);

        WindowPlacement.Clamp(saved, [Primary, LeftSecondary])
            .ShouldBe(new PixelRect(-320, 100, 320, 420));
    }

    [Fact]
    public void Falls_back_to_the_first_monitor_when_none_is_primary()
    {
        var noPrimary = LeftSecondary with { IsPrimary = false };
        var saved = new PixelRect(9000, 9000, 320, 420);

        WindowPlacement.Clamp(saved, [noPrimary])
            .ShouldBe(new PixelRect(-320, 620, 320, 420));
    }

    [Fact]
    public void Dpi_does_not_affect_the_result()
    {
        // Geometry is stored in physical pixels, so Clamp must be DPI-agnostic.
        var saved = new PixelRect(1800, 100, 320, 420);
        var at96 = Primary;
        var at192 = Primary with { Dpi = 192 };

        WindowPlacement.Clamp(saved, [at96])
            .ShouldBe(WindowPlacement.Clamp(saved, [at192]));
    }
}
