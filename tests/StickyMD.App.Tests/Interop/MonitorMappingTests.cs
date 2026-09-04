using Shouldly;
using StickyMD.App.Interop;
using StickyMD.Core.Geometry;

namespace StickyMD.App.Tests.Interop;

public class MonitorMappingTests
{
    private static RawMonitor Raw(
        int x = 0, int y = 0, int w = 2560, int h = 1440,
        uint dpi = 96, bool primary = true, string device = @"\\.\DISPLAY1")
        => new(
            new PixelRect(x, y, w, h),
            new PixelRect(x, y, w, h - 48),
            dpi,
            primary,
            device);

    [Fact]
    public void Dpi_is_carried_through_as_a_raw_value_not_a_scale_factor()
    {
        // MonitorInfo.Dpi is 96 / 120 / 144, matching Plan A's
        // WindowPlacementTests which construct Dpi: 96 and Dpi: 144. A scale
        // factor here would make every DPI-derived calculation 96x too small
        // and nothing would fail loudly.
        var mapped = MonitorEnumerator.Map([Raw(dpi: 144)]);

        mapped[0].Dpi.ShouldBe(144);
    }

    [Fact]
    public void Bounds_and_work_area_are_carried_through_unchanged()
    {
        var raw = Raw(x: 2560, y: 0);

        var mapped = MonitorEnumerator.Map([raw])[0];

        mapped.Bounds.ShouldBe(raw.Bounds);
        mapped.WorkArea.ShouldBe(raw.WorkArea);
    }

    [Fact]
    public void The_primary_flag_and_device_name_survive()
    {
        var mapped = MonitorEnumerator.Map(
        [
            Raw(primary: true, device: @"\\.\DISPLAY1"),
            Raw(x: 2560, primary: false, device: @"\\.\DISPLAY2"),
        ]);

        mapped[0].IsPrimary.ShouldBeTrue();
        mapped[0].DeviceName.ShouldBe(@"\\.\DISPLAY1");
        mapped[1].IsPrimary.ShouldBeFalse();
        mapped[1].DeviceName.ShouldBe(@"\\.\DISPLAY2");
    }

    [Fact]
    public void The_primary_monitor_is_listed_first()
    {
        // WindowPlacement.PickTarget falls back to FirstOrDefault(IsPrimary)
        // and then to monitors[0]. Ordering primary first makes those two
        // fallbacks agree instead of quietly disagreeing.
        var mapped = MonitorEnumerator.Map(
        [
            Raw(x: 2560, primary: false, device: @"\\.\DISPLAY2"),
            Raw(primary: true, device: @"\\.\DISPLAY1"),
        ]);

        mapped[0].DeviceName.ShouldBe(@"\\.\DISPLAY1");
    }

    [Fact]
    public void A_zero_dpi_is_replaced_with_96()
    {
        // GetDpiForMonitor can fail; the caller records 0 rather than
        // guessing. A Dpi of 0 downstream is a division by zero waiting to
        // happen, so it is settled here.
        MonitorEnumerator.Map([Raw(dpi: 0)])[0].Dpi.ShouldBe(96);
    }

    [Fact]
    public void An_empty_device_name_becomes_a_stable_placeholder()
    {
        // notes.json stores the device name. An empty string there would be
        // indistinguishable from "not recorded", and the field is written from
        // v1 specifically so prefer-original-monitor can land later.
        MonitorEnumerator.Map([Raw(device: "")])[0]
            .DeviceName.ShouldBe("(unknown)");
    }

    [Fact]
    public void No_monitors_maps_to_an_empty_list_rather_than_throwing()
    {
        // WindowPlacement.Clamp already returns the saved rect untouched for an
        // empty list. This just must not be the thing that throws.
        MonitorEnumerator.Map([]).ShouldBeEmpty();
    }

    [Fact]
    public void Mapped_monitors_feed_WindowPlacement_unchanged()
    {
        var monitors = MonitorEnumerator.Map(
        [
            Raw(primary: true),
            Raw(x: 2560, primary: false, device: @"\\.\DISPLAY2"),
        ]);

        var offScreen = new PixelRect(9000, 9000, 300, 340);

        var clamped = WindowPlacement.Clamp(offScreen, monitors);

        clamped.ShouldNotBe(offScreen);
        monitors.ShouldContain(m =>
            clamped.X >= m.WorkArea.X && clamped.Right <= m.WorkArea.Right);
    }
}
