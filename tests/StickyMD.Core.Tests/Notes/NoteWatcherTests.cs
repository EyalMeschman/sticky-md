using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Notes;

public class NoteWatcherTests
{
    [Fact]
    public void Raises_ExternalChanged_for_an_outside_write()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.md", "original");
        var changed = new List<string>();

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.ExternalChanged += p => changed.Add(p);

        File.WriteAllText(path, "edited by another app");

        Wait.Until(() => changed.Count > 0, "ExternalChanged to fire");
        changed.Single().ShouldBe(NotePath.Canonical(path));
    }

    [Fact]
    public void Stays_silent_for_a_write_made_through_NoteFile()
    {
        // THE critical test: StickyMD must never react to its own save.
        //
        // Asserting only on ExternalChanged is not enough and used to hide a real
        // bug: File.Replace surfaces an atomic save as two RENAME events, so this
        // test passed even with no ledger wired in. Watch every channel.
        using var dir = new TempDir();
        var path = dir.WriteFile("a.md", "original");
        var ledger = new WriteLedger();
        var fired = new List<string>();

        using var watcher = new NoteWatcher(dir.Path, ledger);
        watcher.ExternalChanged += _ => fired.Add("ExternalChanged");
        watcher.Deleted += _ => fired.Add("Deleted");
        watcher.Renamed += (_, _) => fired.Add("Renamed");
        watcher.Recovered += _ => fired.Add("Recovered");

        var outcome = NoteFile.AtomicWrite(path, "saved by StickyMD", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        Wait.StaysFalse(() => fired.Count > 0,
            "no event at all may fire for StickyMD's own save");
    }

    [Fact]
    public void Coalesces_a_burst_of_writes_into_one_event()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.md", "original");
        var count = 0;

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger(), debounceMs: 400);
        watcher.ExternalChanged += _ => Interlocked.Increment(ref count);

        for (var i = 0; i < 6; i++) File.WriteAllText(path, $"edit {i}");

        Wait.Until(() => count > 0, "at least one event");
        Thread.Sleep(300);
        count.ShouldBe(1);
    }

    [Fact]
    public void Raises_Deleted_when_the_file_disappears()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.md", "original");
        var deleted = new List<string>();

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.Deleted += p => deleted.Add(p);

        File.Delete(path);

        Wait.Until(() => deleted.Count > 0, "Deleted to fire");
        deleted[0].ShouldBe(NotePath.Canonical(path));
    }

    [Fact]
    public void Raises_Renamed_with_both_paths_for_an_in_folder_rename()
    {
        using var dir = new TempDir();
        var oldPath = dir.WriteFile("old.md", "body");
        var newPath = dir.File("new.md");
        (string Old, string New)? seen = null;

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.Renamed += (o, n) => seen = (o, n);

        File.Move(oldPath, newPath);

        Wait.Until(() => seen is not null, "Renamed to fire");
        seen!.Value.Old.ShouldBe(NotePath.Canonical(oldPath));
        seen!.Value.New.ShouldBe(NotePath.Canonical(newPath));
    }

    [Fact]
    public void A_move_out_of_the_folder_is_reported_as_a_deletion()
    {
        // Spec 8.2: FileSystemWatcher only reports Renamed when both paths are
        // inside the watched directory.
        using var dir = new TempDir();
        using var elsewhere = new TempDir();
        var path = dir.WriteFile("a.md", "body");
        var deleted = false;
        var renamed = false;

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.Deleted += _ => deleted = true;
        watcher.Renamed += (_, _) => renamed = true;

        File.Move(path, elsewhere.File("a.md"));

        Wait.Until(() => deleted, "Deleted to fire for a move-out");
        renamed.ShouldBeFalse();
    }

    [Fact]
    public void Renaming_a_note_to_a_non_markdown_name_is_reported_as_a_deletion()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.md", "body");
        var deleted = new List<string>();
        var renamed = false;

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.Deleted += p => deleted.Add(p);
        watcher.Renamed += (_, _) => renamed = true;

        // The note leaves the watched set. A consumer must learn it is gone, or its
        // index entry and window go stale with no way to reconcile.
        File.Move(path, dir.File("a.txt"));

        Wait.Until(() => deleted.Count > 0, "Deleted for a note renamed out of the set");
        deleted[0].ShouldBe(NotePath.Canonical(path));
        renamed.ShouldBeFalse();
    }

    private static void RaiseRenamed(NoteWatcher watcher, string oldFullPath, string newFullPath)
    {
        var method = typeof(NoteWatcher).GetMethod(
            "OnRenamed",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OnRenamed not found.");

        var args = new RenamedEventArgs(
            WatcherChangeTypes.Renamed,
            Path.GetDirectoryName(newFullPath)!,
            Path.GetFileName(newFullPath),
            Path.GetFileName(oldFullPath));

        method.Invoke(watcher, [null!, args]);
    }

    [Fact]
    public void A_temp_to_real_rename_is_routed_through_the_ledger_not_reported_as_a_rename()
    {
        // THE fix-1 defect, tested directly: File.Replace's "a.md.stickymd-tmp ->
        // a.md" rename must never surface as Renamed -- it must go through Enqueue
        // so the write ledger gets a chance to suppress it. Driven via reflection,
        // like RaiseError above, so this does not depend on the OS actually
        // producing this exact rename sequence during the test run.
        using var dir = new TempDir();
        var realPath = dir.WriteFile("a.md", "content");
        var renamed = false;
        var changed = new List<string>();

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.Renamed += (_, _) => renamed = true;
        watcher.ExternalChanged += p => changed.Add(p);

        RaiseRenamed(watcher, dir.File("a.md.stickymd-tmp"), realPath);

        Wait.Until(() => changed.Count > 0,
            "the tmp -> real transition must be dispatched through Enqueue, not Renamed");
        renamed.ShouldBeFalse();
    }

    [Fact]
    public void A_rename_whose_paths_are_unusable_is_dropped_rather_than_reported()
    {
        // OnRenamed's "TryCanonical failed, so drop it" branch. Both sides end
        // in .md, so this gets past the tmp-file and out-of-set routing and
        // reaches the canonicalisation guard -- where an embedded NUL cannot
        // become a path. The requirement is that ONE unusable event costs that
        // event and nothing else: no throw out of the watcher callback (which
        // is unhandled from a FileSystemWatcher thread), and no Renamed
        // carrying a path no consumer could re-key to.
        using var dir = new TempDir();
        dir.WriteFile("a.md", "content");

        var fired = new List<string>();

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.Renamed += (_, _) => fired.Add("Renamed");
        watcher.ExternalChanged += _ => fired.Add("ExternalChanged");
        watcher.Deleted += _ => fired.Add("Deleted");

        Should.NotThrow(() => RaiseRenamed(
            watcher, dir.File("a\0b.md"), dir.File("c.md")));

        Wait.StaysFalse(() => fired.Count > 0,
            "an unusable rename path must cost that one event and nothing more");
    }

    [Fact]
    public void Ignores_files_that_are_not_markdown()
    {
        using var dir = new TempDir();
        dir.WriteFile("a.md", "body");
        var fired = false;

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.ExternalChanged += _ => fired = true;

        File.WriteAllText(dir.File("notes.txt"), "not a note");

        Wait.StaysFalse(() => fired, "a .txt file must not raise a note event");
    }

    [Fact]
    public void Raises_ExternalChanged_for_a_newly_created_md_file()
    {
        using var dir = new TempDir();
        var changed = new List<string>();

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.ExternalChanged += p => changed.Add(p);

        File.WriteAllText(dir.File("fresh.md"), "dropped in by Obsidian");

        Wait.Until(() => changed.Count > 0, "ExternalChanged for a new file");
    }

    [Fact]
    public void Stops_raising_events_after_disposal()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.md", "original");
        var fired = false;

        var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.ExternalChanged += _ => fired = true;
        watcher.Dispose();

        File.WriteAllText(path, "after disposal");

        Wait.StaysFalse(() => fired, "a disposed watcher must be inert");
    }

    [Fact]
    public void Construction_on_a_missing_directory_does_not_throw()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Path, "not-created-yet");

        Should.NotThrow(() =>
        {
            using var watcher = new NoteWatcher(missing, new WriteLedger());
        });
    }

    private static void RaiseError(NoteWatcher watcher, Exception error)
    {
        var method = typeof(NoteWatcher).GetMethod(
            "OnError",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OnError not found.");

        method.Invoke(watcher, [null!, new ErrorEventArgs(error)]);
    }

    [Fact]
    public void Recovered_fires_and_the_watcher_keeps_working_after_an_error()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.md", "original");
        Exception? recovered = null;
        var changed = false;

        using var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.Recovered += ex => recovered = ex;
        watcher.ExternalChanged += _ => changed = true;

        RaiseError(watcher, new InvalidOperationException("buffer overflow"));

        recovered.ShouldNotBeNull();
        recovered.Message.ShouldBe("buffer overflow");

        // The point of recovery: the recreated watcher must still deliver events.
        File.WriteAllText(path, "edited after recovery");
        Wait.Until(() => changed, "ExternalChanged after the watcher recovered");
    }

    [Fact]
    public void A_late_error_callback_after_disposal_is_a_no_op()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("a.md", "original");
        var recoveredFired = false;
        var fired = false;

        var watcher = new NoteWatcher(dir.Path, new WriteLedger());
        watcher.Recovered += _ => recoveredFired = true;
        watcher.ExternalChanged += _ => fired = true;
        watcher.Renamed += (_, _) => fired = true;
        watcher.Deleted += _ => fired = true;
        watcher.Dispose();

        // Verifies OnError's FIRST locked _disposed check: an Error callback that
        // arrives entirely after Dispose has returned must not restart anything.
        //
        // NOT COVERED, and not coverable without adding a test seam to production
        // code: the actual cross-thread race, where OnError is already past its
        // _disposed read when Dispose completes, and the SECOND locked check is what
        // prevents a resurrected watcher. This call is sequential and same-thread, so
        // it passes against the unguarded version too. The second check is reasoned
        // correct, not test-proven -- do not delete it on the strength of this test.
        RaiseError(watcher, new InvalidOperationException("late error"));

        recoveredFired.ShouldBeFalse();
        File.Move(path, dir.File("renamed.md"));
        Wait.StaysFalse(() => fired, "a disposed watcher must stay inert after a late Error");
    }
}
