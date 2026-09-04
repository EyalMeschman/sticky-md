using Shouldly;
using StickyMD.Core.Diagnostics;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Diagnostics;

public class DiagnosticsLogTests
{
    [Fact]
    public void Write_creates_the_file_and_the_directory_under_it()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "nested", "diagnostics.log");

        DiagnosticsLog.Write(path, "hello");

        File.ReadAllText(path).ShouldContain("hello");
    }

    [Fact]
    public void Write_appends_rather_than_replacing()
    {
        using var dir = new TempDir();
        var path = dir.File("diagnostics.log");

        DiagnosticsLog.Write(path, "first");
        DiagnosticsLog.Write(path, "second");

        var text = File.ReadAllText(path);
        text.ShouldContain("first");
        text.ShouldContain("second");
    }

    [Fact]
    public void Each_line_carries_a_utc_timestamp()
    {
        using var dir = new TempDir();
        var path = dir.File("diagnostics.log");

        DiagnosticsLog.Write(path, "hello");

        File.ReadAllText(path).ShouldContain("Z ");
    }

    [Fact]
    public void WriteAll_writes_a_heading_and_one_line_per_item()
    {
        using var dir = new TempDir();
        var path = dir.File("diagnostics.log");

        DiagnosticsLog.WriteAll(path, "notes.json corrections", ["a", "b"]);

        var lines = File.ReadAllLines(path);
        lines.Length.ShouldBe(3);
        lines[0].ShouldContain("notes.json corrections");
        lines[1].ShouldContain("a");
        lines[2].ShouldContain("b");
    }

    [Fact]
    public void WriteAll_with_no_items_writes_nothing_at_all()
    {
        using var dir = new TempDir();
        var path = dir.File("diagnostics.log");

        DiagnosticsLog.WriteAll(path, "nothing to say", []);

        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void An_oversized_log_is_rotated_rather_than_growing_without_bound()
    {
        using var dir = new TempDir();
        var path = dir.File("diagnostics.log");
        File.WriteAllText(path, new string('x', DiagnosticsLog.MaxBytes + 1));

        DiagnosticsLog.Write(path, "after rotation");

        File.Exists(path + ".1").ShouldBeTrue();
        File.ReadAllText(path).ShouldContain("after rotation");
        File.ReadAllText(path).Length.ShouldBeLessThan(DiagnosticsLog.MaxBytes);
    }

    [Fact]
    public void A_write_to_an_impossible_path_is_swallowed()
    {
        // This is the LAST-RESORT reporting channel. If it throws, it takes out
        // whatever error path was trying to report through it.
        Should.NotThrow(() => DiagnosticsLog.Write("C:\\a\0b\\log.txt", "x"));
    }
}
