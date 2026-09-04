using System.IO;
using Shouldly;
using StickyMD.App.Services;
using StickyMD.App.Tests.TestSupport;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;

namespace StickyMD.App.Tests.Services;

public class SaveCoordinatorTests
{
    private const string NotePath = @"C:\Notes\standup.md";

    private static (SaveCoordinator Coordinator,
                    FakeNoteFileGateway Gateway,
                    WriteLedger Ledger,
                    RecoveryStore Recovery,
                    TempRecoveryDir Dir) Build(
        IReadOnlyList<int>? backoff = null)
    {
        var dir = new TempRecoveryDir();
        var gateway = new FakeNoteFileGateway();
        var ledger = new WriteLedger();
        var recovery = new RecoveryStore(dir.Path);

        var coordinator = new SaveCoordinator(
            NotePath, gateway, ledger, recovery,
            diagnosticsFile: Path.Combine(dir.Path, "diagnostics.log"),
            // Zeros so the retry SCHEDULE is exercised without the test
            // waiting 1.3 seconds for it.
            retryBackoffMs: backoff ?? [0, 0, 0]);

        return (coordinator, gateway, ledger, recovery, dir);
    }

    /// <summary>A temp directory for recovery snapshots.</summary>
    private sealed class TempRecoveryDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "stickymd-save", Guid.NewGuid().ToString("N"));

        public TempRecoveryDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task A_flush_with_no_changes_does_not_write()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        var outcome = await coordinator.FlushAsync();

        outcome.Status.ShouldBe(SaveStatus.NothingToDo);
        gateway.WriteAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task A_dirty_buffer_is_written_once()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        coordinator.MarkDirty("# hello");
        var outcome = await coordinator.FlushAsync();

        outcome.Status.ShouldBe(SaveStatus.Saved);
        outcome.Attempts.ShouldBe(1);
        gateway.WrittenTexts.ShouldBe(["# hello"]);
    }

    [Fact]
    public async Task A_second_flush_after_a_successful_save_writes_nothing()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        coordinator.MarkDirty("# hello");
        await coordinator.FlushAsync();
        var second = await coordinator.FlushAsync();

        second.Status.ShouldBe(SaveStatus.NothingToDo);
        gateway.WriteAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task Only_the_latest_text_is_written()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        coordinator.MarkDirty("one");
        coordinator.MarkDirty("two");
        coordinator.MarkDirty("three");
        await coordinator.FlushAsync();

        gateway.WrittenTexts.ShouldBe(["three"]);
    }

    [Fact]
    public async Task A_successful_save_records_the_write_in_the_ledger()
    {
        var (coordinator, gateway, ledger, _, dir) = Build();
        using var _d = dir;

        coordinator.MarkDirty("# hello");
        var outcome = await coordinator.FlushAsync();

        // Without this the watcher reports StickyMD's own save as an external
        // change, and the note reloads itself in a loop.
        var fingerprint = ledger.Peek(NotePath);
        fingerprint.ShouldNotBeNull();
        fingerprint.ContentHash.ShouldBe(outcome.DiskHash);
    }

    [Fact]
    public async Task Two_failures_then_success_reports_three_attempts()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        gateway.FailNextWrites(2);
        coordinator.MarkDirty("# hello");

        var outcome = await coordinator.FlushAsync();

        outcome.Status.ShouldBe(SaveStatus.Saved);
        outcome.Attempts.ShouldBe(3);
        gateway.Content.ShouldBe("# hello");
    }

    [Fact]
    public async Task Four_failures_exhausts_the_retries_and_reports_failure()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        // One initial attempt plus three retries = four writes.
        gateway.FailNextWrites(4);
        coordinator.MarkDirty("# hello");

        var outcome = await coordinator.FlushAsync();

        outcome.Status.ShouldBe(SaveStatus.Failed);
        outcome.Attempts.ShouldBe(4);
        outcome.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_exhausted_save_writes_a_recovery_snapshot_holding_the_buffer()
    {
        var (coordinator, gateway, _, recovery, dir) = Build();
        using var _d = dir;

        gateway.FailNextWrites(4);
        coordinator.MarkDirty("text that must not be lost");

        await coordinator.FlushAsync();

        var envelope = recovery.TryLoad(NotePath);
        envelope.ShouldNotBeNull();
        envelope.Content.ShouldBe("text that must not be lost");
        envelope.OriginalPath.ShouldBe(NotePath);
    }

    [Fact]
    public async Task A_transient_failure_that_clears_leaves_no_snapshot_behind()
    {
        var (coordinator, gateway, _, recovery, dir) = Build();
        using var _d = dir;

        // Snapshots are written only AFTER the retries are exhausted. A
        // OneDrive lock that clears on attempt two must not leave stale text
        // that a later startup would offer to restore.
        gateway.FailNextWrites(2);
        coordinator.MarkDirty("# hello");

        await coordinator.FlushAsync();

        recovery.TryLoad(NotePath).ShouldBeNull();
    }

    [Fact]
    public async Task A_successful_save_clears_an_earlier_snapshot()
    {
        var (coordinator, gateway, _, recovery, dir) = Build();
        using var _d = dir;

        gateway.FailNextWrites(4);
        coordinator.MarkDirty("first attempt");
        await coordinator.FlushAsync();
        recovery.TryLoad(NotePath).ShouldNotBeNull();

        coordinator.MarkDirty("second attempt");
        var outcome = await coordinator.FlushAsync();

        outcome.Status.ShouldBe(SaveStatus.Saved);
        recovery.TryLoad(NotePath).ShouldBeNull();
    }

    [Fact]
    public async Task The_buffer_survives_a_failed_save_so_a_retry_can_still_write_it()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        gateway.FailNextWrites(4);
        coordinator.MarkDirty("precious");
        await coordinator.FlushAsync();

        coordinator.IsDirty.ShouldBeTrue("never lose text");

        var retry = await coordinator.FlushAsync();

        retry.Status.ShouldBe(SaveStatus.Saved);
        gateway.Content.ShouldBe("precious");
    }

    [Fact]
    public async Task An_unauthorized_access_failure_is_retried_like_an_io_one()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        gateway.FailNextWrites(2, new UnauthorizedAccessException("AV scan"));
        coordinator.MarkDirty("# hello");

        (await coordinator.FlushAsync()).Status.ShouldBe(SaveStatus.Saved);
    }

    [Fact]
    public async Task An_encoding_failure_is_NOT_retried()
    {
        var (coordinator, gateway, _, recovery, dir) = Build();
        using var _d = dir;

        // A lone surrogate in the buffer throws from NoteFile's strict
        // encoder, and it will throw identically on every retry. Retrying is
        // three wasted delays before the same answer -- but the snapshot still
        // has to be written, because that text cannot reach the file at all.
        gateway.FailNextWrites(1, new System.Text.EncoderFallbackException());
        coordinator.MarkDirty("bad \ud800 text");

        var outcome = await coordinator.FlushAsync();

        outcome.Status.ShouldBe(SaveStatus.Failed);
        outcome.Attempts.ShouldBe(1);
        recovery.TryLoad(NotePath).ShouldNotBeNull();
    }

    [Fact]
    public async Task An_unrecognised_exception_is_treated_as_permanent_not_left_to_escape()
    {
        var (coordinator, gateway, _, recovery, dir) = Build();
        using var _d = dir;

        // ObjectDisposedException matches neither IsRetryable nor IsPermanent.
        // Letting it propagate out of FlushAsync would skip Fail() entirely --
        // no snapshot, and an unhandled exception on the UI thread, since every
        // real caller is an async void handler with no try/catch.
        gateway.FailNextWrites(1, new ObjectDisposedException("gateway"));
        coordinator.MarkDirty("text that must survive an unknown failure");

        var outcome = await coordinator.FlushAsync();

        outcome.Status.ShouldBe(SaveStatus.Failed);
        outcome.Attempts.ShouldBe(1);
        recovery.TryLoad(NotePath).ShouldNotBeNull();
    }

    [Fact]
    public async Task The_Saved_event_fires_with_the_outcome()
    {
        var (coordinator, _, _, _, dir) = Build();
        using var _d = dir;

        SaveOutcome? seen = null;
        coordinator.Saved += o => seen = o;

        coordinator.MarkDirty("# hello");
        await coordinator.FlushAsync();

        seen.ShouldNotBeNull();
        seen.Status.ShouldBe(SaveStatus.Saved);
    }

    [Fact]
    public async Task The_on_disk_format_is_preserved_across_a_save()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        var onDisk = new NoteFormat(
            NoteEncoding.Utf8Bom, NoteNewline.Lf, TrailingNewline: false);
        gateway.Format = onDisk;

        // The window reads the file and hands the format over; the coordinator's
        // job is only to REPRODUCE it. Adopting is what a real caller does
        // (NoteWindow.LoadFromDisk), so the test has to do it too.
        coordinator.AdoptFromDisk(gateway.Read(NotePath));

        coordinator.MarkDirty("# hello");
        await coordinator.FlushAsync();

        // Assert the WRITE used it. That is the property that stops a save from
        // silently reformatting the user's file -- checking the coordinator's own
        // property would only prove it stored what it was given.
        coordinator.Format.ShouldBe(onDisk);
        gateway.WrittenFormats.ShouldBe([onDisk]);
    }

    [Fact]
    public async Task A_coordinator_that_never_adopted_a_format_writes_the_canonical_one()
    {
        // A note StickyMD created itself has no prior format to preserve, so
        // canonical (UTF-8 no BOM, CRLF, trailing newline) is correct. The
        // coordinator does NOT read the file to discover a format -- that would
        // put an I/O on every autosave tick, and discovery belongs to the window.
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        gateway.Format = new NoteFormat(
            NoteEncoding.Utf16Le, NoteNewline.Lf, TrailingNewline: false);

        coordinator.MarkDirty("# hello");
        await coordinator.FlushAsync();

        gateway.WrittenFormats.ShouldBe([NoteFormat.Canonical]);
    }

    [Fact]
    public async Task Concurrent_flushes_do_not_write_twice()
    {
        var (coordinator, gateway, _, _, dir) = Build();
        using var _d = dir;

        coordinator.MarkDirty("# hello");

        var a = coordinator.FlushAsync();
        var b = coordinator.FlushAsync();
        await Task.WhenAll(a, b);

        // The 500ms debounce timer and an explicit flush on blur can land
        // together. Two concurrent AtomicWrites to one path race over the same
        // temp file.
        gateway.WriteAttempts.ShouldBe(1);
    }
}
