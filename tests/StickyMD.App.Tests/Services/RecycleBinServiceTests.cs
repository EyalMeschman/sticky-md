using System.IO;
using Shouldly;
using StickyMD.App.Services;

namespace StickyMD.App.Tests.Services;

/// <summary>
/// These touch the real Recycle Bin, on purpose. A faked shell call would
/// verify nothing about the one thing that matters -- that the file is
/// RECOVERABLE afterwards.
/// </summary>
public class RecycleBinServiceTests
{
    private static string TempNote(string content = "# note")
    {
        var dir = Path.Combine(Path.GetTempPath(), "stickymd-recycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "note.md");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_real_file_is_removed_from_disk()
    {
        // THIS LEAVES A FILE IN THE DEVELOPER'S RECYCLE BIN on every run, and
        // that is correct rather than a leak to fix: the one property worth
        // testing here is that the file is RECOVERABLE afterwards, and no fake
        // of SHFileOperationW can establish it. The accumulation is a handful
        // of empty note.md files per test run under a
        // %TEMP%\stickymd-recycle\<guid> folder -- expected, and safe to empty
        // whenever it becomes annoying. Do not "fix" this by faking the shell
        // call.
        var path = TempNote();

        var result = new RecycleBinService().SendToRecycleBin(path);

        result.Outcome.ShouldBe(DeletionOutcome.Deleted);
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void A_missing_file_reports_NotFound_rather_than_failing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.md");

        new RecycleBinService().SendToRecycleBin(path)
            .Outcome.ShouldBe(DeletionOutcome.NotFound);
    }

    [Fact]
    public void A_non_canonical_path_is_still_deleted()
    {
        var path = TempNote();
        var messy = Path.Combine(Path.GetDirectoryName(path)!, ".", "note.md");

        new RecycleBinService().SendToRecycleBin(messy)
            .Outcome.ShouldBe(DeletionOutcome.Deleted);
    }

    [Fact]
    public void A_locked_file_reports_Failed_with_a_message_and_leaves_it_alone()
    {
        var path = TempNote();

        using (var hold = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = new RecycleBinService().SendToRecycleBin(path);

            result.Outcome.ShouldBe(DeletionOutcome.Failed);
            result.Message.ShouldNotBeNullOrWhiteSpace();
        }

        File.Exists(path).ShouldBeTrue("never destroy a file");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unusable_path_reports_Failed_without_throwing(string? path)
        => new RecycleBinService().SendToRecycleBin(path!)
            .Outcome.ShouldBe(DeletionOutcome.Failed);
}
