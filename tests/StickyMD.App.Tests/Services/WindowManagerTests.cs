using System.IO;
using Shouldly;
using StickyMD.App.Interop;
using StickyMD.App.Services;
using StickyMD.App.Tests.TestSupport;
using StickyMD.Core.Geometry;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;

namespace StickyMD.App.Tests.Services;

public class WindowManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "stickymd-wm", Guid.NewGuid().ToString("N"));

    private readonly FakeNoteWindowFactory _factory = new();
    private readonly FakeMonitorProvider _monitors = FakeMonitorProvider.TwoAt100Percent();
    private readonly FakeFileDeletionService _deleter = new();

    private NoteIndexStore _indexStore = null!;
    private WindowManager _manager = null!;

    public WindowManagerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _manager?.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class FixedTheme : ISystemTheme
    {
        public ThemeMode Mode { get; set; } = ThemeMode.Light;

        public ThemeMode Resolve(ThemePreference preference) => Mode;

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();
    }

    private sealed class FakeFileDeletionService : IFileDeletionService
    {
        public List<string> Deleted { get; } = [];

        public DeletionOutcome Next { get; set; } = DeletionOutcome.Deleted;

        public DeletionResult SendToRecycleBin(string path)
        {
            if (Next == DeletionOutcome.Deleted)
            {
                Deleted.Add(path);
                if (File.Exists(path)) File.Delete(path);
            }

            return new DeletionResult(
                Next, Next == DeletionOutcome.Failed ? "nope" : null);
        }
    }

    private string WriteNote(string name, string content = "# note")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return NotePath.Canonical(path);
    }

    private WindowManager Build(NoteIndex? seed = null)
    {
        _indexStore = new NoteIndexStore(Path.Combine(_root, "notes.json"));
        if (seed is not null) _indexStore.Save(seed);

        _manager = new WindowManager(
            new NoteRepository(_root, new FixedClock(new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc))),
            _indexStore,
            new SettingsStore(Path.Combine(_root, "settings.json")),
            _factory,
            _monitors,
            new FixedTheme(),
            _deleter,
            new WriteLedger(),
            new RecoveryStore(Path.Combine(_root, "recovery")),
            diagnosticsFile: Path.Combine(_root, "diagnostics.log"));

        return _manager;
    }

    private static NoteState StateAt(int x, int y, bool isOpen = true) => new(
        X: x, Y: y, W: 300, H: 340,
        Monitor: null,
        Color: NoteColor.Yellow,
        Opacity: 1.0,
        AlwaysOnTop: false,
        IsOpen: isOpen,
        LastOpenedUtc: new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));

    // ---- The three-state model -------------------------------------------

    [Fact]
    public void RestoreOpenNotes_opens_only_the_notes_marked_open()
    {
        var open = WriteNote("open.md");
        var closed = WriteNote("closed.md");

        var index = new NoteIndex();
        index.Notes[open] = StateAt(100, 100);
        index.Notes[closed] = StateAt(200, 200, isOpen: false);

        Build(index).RestoreOpenNotes();

        _factory.Created.Count.ShouldBe(1);
        _factory.Created[0].NotePath.ShouldBe(open);
    }

    [Fact]
    public void RestoreOpenNotes_spawns_no_window_for_a_note_that_is_merely_present()
    {
        // Pointing StickyMD at an Obsidian vault must not carpet the desktop.
        WriteNote("a.md");
        WriteNote("b.md");
        WriteNote("c.md");

        Build().RestoreOpenNotes();

        _factory.Created.ShouldBeEmpty();
    }

    [Fact]
    public void The_close_glyph_clears_isOpen_and_disposes_the_window()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(100, 100);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(path).RaiseClose();

        _indexStore.Load().Notes[path].IsOpen.ShouldBeFalse();
        _factory.For(path).IsDisposed.ShouldBeTrue();
        manager.OpenPaths.ShouldBeEmpty();
    }

    [Fact]
    public void HideAll_hides_every_window_and_leaves_isOpen_alone()
    {
        var a = WriteNote("a.md");
        var b = WriteNote("b.md");
        var index = new NoteIndex();
        index.Notes[a] = StateAt(10, 10);
        index.Notes[b] = StateAt(20, 20);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        manager.HideAll();

        _factory.Created.ShouldAllBe(w => !w.IsVisible);
        _indexStore.Load().Notes.Values.ShouldAllBe(s => s.IsOpen);
        manager.OpenPaths.Count.ShouldBe(2, "hidden is not closed");
    }

    [Fact]
    public void ShowAll_brings_hidden_windows_back()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();
        manager.HideAll();

        manager.ShowAll();

        _factory.For(path).IsVisible.ShouldBeTrue();
    }

    [Fact]
    public void ShutdownWithoutClosingNotes_leaves_every_isOpen_set()
    {
        // THE HEADLINE HAZARD. WPF closes every window during application
        // shutdown. If the close-glyph logic ran on Window.Closing, choosing
        // Exit would clear isOpen on every note and the next boot would
        // restore none of them -- breaking success criterion 3.
        var a = WriteNote("a.md");
        var b = WriteNote("b.md");
        var index = new NoteIndex();
        index.Notes[a] = StateAt(10, 10);
        index.Notes[b] = StateAt(20, 20);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        manager.ShutdownWithoutClosingNotes();

        var reloaded = _indexStore.Load();
        reloaded.Notes[a].IsOpen.ShouldBeTrue();
        reloaded.Notes[b].IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void ShutdownWithoutClosingNotes_saves_every_dirty_buffer_first()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        manager.ShutdownWithoutClosingNotes();

        _factory.For(path).WasSaved.ShouldBeTrue("never lose text");
    }

    [Fact]
    public void A_late_state_change_while_closing_cannot_reopen_the_note()
    {
        // NoteWindow.Dispose() -> Close() -> Closing -> StateChanged is a real
        // cascade in the shipped window. WindowManager must not let a late
        // event from a window it is in the middle of closing resurrect the
        // very state CloseNote is about to clear.
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(path).StateChangedOnDispose =
            StateAt(999, 10, isOpen: true);

        _factory.For(path).RaiseClose();

        // Both assertions matter. CloseNote's own trailing statement
        // ("...with { IsOpen = false }") forces IsOpen back to false no
        // matter what the late event wrote, so IsOpen alone would pass even
        // with Detach missing -- masking a real leak. X pins that the late
        // event's geometry never reached the index at all.
        var reloaded = _indexStore.Load().Notes[path];
        reloaded.IsOpen.ShouldBeFalse();
        reloaded.X.ShouldBe(10);
    }

    [Fact]
    public void A_late_state_change_while_deleting_cannot_resurrect_the_entry()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(path).StateChangedOnDispose =
            StateAt(999, 10, isOpen: true);

        _factory.For(path).RaiseDelete();

        _indexStore.Load().Notes.ShouldNotContainKey(path);
    }

    [Fact]
    public void A_late_state_change_during_shutdown_cannot_overwrite_the_fresh_geometry()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        var fake = _factory.For(path);
        fake.Bounds = new PixelRect(555, 10, 300, 340);
        fake.StateChangedOnDispose = StateAt(4242, 10, isOpen: true);

        manager.ShutdownWithoutClosingNotes();

        _indexStore.Load().Notes[path].X.ShouldBe(555);
    }

    // ---- Path identity ---------------------------------------------------

    [Fact]
    public void Opening_a_note_twice_focuses_the_existing_window()
    {
        var path = WriteNote("n.md");
        var manager = Build();

        manager.OpenNote(path);
        manager.OpenNote(path);

        _factory.Created.Count.ShouldBe(1, "never two windows on one file");
        _factory.For(path).WasFocused.ShouldBeTrue();
    }

    [Fact]
    public void Opening_the_same_note_through_a_different_spelling_focuses_it()
    {
        // Finding 1, as a behaviour test. Before NotePath, a path from the
        // repository and one from the watcher were different dictionary keys
        // and this produced a second window.
        var path = WriteNote("Standup.md");
        var manager = Build();

        manager.OpenNote(path);
        manager.OpenNote(Path.Combine(_root, "sub", "..", "STANDUP.MD"));

        _factory.Created.Count.ShouldBe(1);
    }

    [Fact]
    public void OpenNote_refuses_a_path_that_is_not_a_file()
    {
        var manager = Build();

        manager.OpenNote(Path.Combine(_root, "absent.md"));

        _factory.Created.ShouldBeEmpty();
    }

    // ---- Geometry --------------------------------------------------------

    [Fact]
    public void A_saved_rect_that_is_fully_visible_is_restored_untouched()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(1200, 80);

        Build(index).RestoreOpenNotes();

        var applied = _factory.For(path).State;
        applied.X.ShouldBe(1200);
        applied.Y.ShouldBe(80);
    }

    [Fact]
    public void A_rect_on_a_monitor_that_is_gone_is_clamped_onto_a_remaining_one()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        // On DISPLAY2, which the provider below does not have.
        index.Notes[path] = StateAt(3000, 200);

        _monitors.Monitors = FakeMonitorProvider.OnlyPrimary().Monitors;

        Build(index).RestoreOpenNotes();

        var applied = _factory.For(path).State;
        applied.X.ShouldBeLessThan(2560);
        applied.X.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void A_state_change_from_a_window_is_persisted()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(path).RaiseStateChanged(
            StateAt(10, 10) with { X = 777, Color = NoteColor.Blue });

        var saved = _indexStore.Load().Notes[path];
        saved.X.ShouldBe(777);
        saved.Color.ShouldBe(NoteColor.Blue);
    }

    [Fact]
    public void OnDisplaySettingsChanged_reclamps_a_note_that_went_off_screen()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(3000, 200);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(path).Bounds = new PixelRect(3000, 200, 300, 340);
        _monitors.Monitors = FakeMonitorProvider.OnlyPrimary().Monitors;

        manager.OnDisplaySettingsChanged();

        _factory.For(path).StatesApplied.Last().X.ShouldBeLessThan(2560);
    }

    [Fact]
    public void OnDisplaySettingsChanged_leaves_an_on_screen_note_where_it_is()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(100, 100);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        var before = _factory.For(path).StatesApplied.Count;
        _factory.For(path).Bounds = new PixelRect(100, 100, 300, 340);

        manager.OnDisplaySettingsChanged();

        // Reapplying a rect that is already correct would move the window by a
        // pixel on every display change.
        _factory.For(path).StatesApplied.Count.ShouldBe(before);
    }

    // ---- Watcher ---------------------------------------------------------

    [Fact]
    public void An_external_change_reaches_the_open_window()
    {
        var path = WriteNote("n.md", "# before");
        var manager = Build();
        manager.OpenNote(path);

        File.WriteAllText(path, "# after");
        manager.OnExternalChanged(path);

        _factory.For(path).ExternalContentApplied.ShouldContain("# after");
    }

    [Fact]
    public void An_external_change_to_a_note_with_no_window_is_ignored()
    {
        var path = WriteNote("n.md");
        var manager = Build();

        Should.NotThrow(() => manager.OnExternalChanged(path));
        _factory.Created.ShouldBeEmpty();
    }

    [Fact]
    public void A_deletion_notifies_the_window_and_does_not_clear_isOpen()
    {
        // The file is gone; the note is not closed. The user still gets to
        // choose Recreate, and closing it for them would discard the buffer.
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        File.Delete(path);
        manager.OnDeleted(path);

        _factory.For(path).WasToldFileDeleted.ShouldBeTrue();
        _factory.For(path).IsDisposed.ShouldBeFalse();
        _indexStore.Load().Notes[path].IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void A_rename_re_keys_the_index_and_retargets_the_window()
    {
        var oldPath = WriteNote("old.md");
        var index = new NoteIndex();
        index.Notes[oldPath] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        var newPath = NotePath.Canonical(Path.Combine(_root, "new.md"));
        File.Move(oldPath, newPath);

        manager.OnRenamed(oldPath, newPath);

        var reloaded = _indexStore.Load();
        reloaded.Notes.ShouldNotContainKey(oldPath);
        reloaded.Notes.ShouldContainKey(newPath);

        _factory.Created[0].RenamesApplied.ShouldContain(newPath);
        manager.OpenPaths.ShouldContain(newPath);
        manager.OpenPaths.ShouldNotContain(oldPath);
    }

    [Fact]
    public void Watcher_recovery_re_reads_every_open_note()
    {
        // NoteWatcher deliberately does not decide which notes to re-read --
        // it has no idea which are open. Events may have been dropped while it
        // was down, so every open note is re-read.
        var a = WriteNote("a.md", "# a1");
        var b = WriteNote("b.md", "# b1");
        var closed = WriteNote("c.md", "# c1");

        var index = new NoteIndex();
        index.Notes[a] = StateAt(10, 10);
        index.Notes[b] = StateAt(20, 20);
        index.Notes[closed] = StateAt(30, 30, isOpen: false);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        File.WriteAllText(a, "# a2");
        File.WriteAllText(b, "# b2");
        File.WriteAllText(closed, "# c2");

        manager.OnWatcherRecovered(new IOException("buffer overflow"));

        _factory.For(a).ExternalContentApplied.ShouldContain("# a2");
        _factory.For(b).ExternalContentApplied.ShouldContain("# b2");
        _factory.Created.Count.ShouldBe(2, "a closed note has no window to re-read into");
    }

    // ---- Delete ----------------------------------------------------------

    [Fact]
    public void Delete_sends_the_file_to_the_recycle_bin_and_removes_the_entry()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(path).RaiseDelete();

        _deleter.Deleted.ShouldContain(path);
        _factory.For(path).IsDisposed.ShouldBeTrue();
        _indexStore.Load().Notes.ShouldNotContainKey(path);
    }

    [Fact]
    public void A_failed_delete_leaves_the_note_open_and_the_entry_intact()
    {
        // Never destroy a file, and never pretend to. Closing the window on a
        // failed delete would leave the file behind with nothing on screen.
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _deleter.Next = DeletionOutcome.Failed;
        _factory.For(path).RaiseDelete();

        _factory.For(path).IsDisposed.ShouldBeFalse();
        _indexStore.Load().Notes.ShouldContainKey(path);
    }

    [Fact]
    public void A_cancelled_delete_changes_nothing()
    {
        var path = WriteNote("n.md");
        var index = new NoteIndex();
        index.Notes[path] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _deleter.Next = DeletionOutcome.Cancelled;
        _factory.For(path).RaiseDelete();

        _factory.For(path).IsDisposed.ShouldBeFalse();
        _indexStore.Load().Notes.ShouldContainKey(path);
    }

    // ---- Rename request from the menu ------------------------------------

    [Fact]
    public void A_rename_request_moves_the_file_and_re_keys_the_index()
    {
        var oldPath = WriteNote("old.md");
        var index = new NoteIndex();
        index.Notes[oldPath] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(oldPath).RaiseRename("renamed");

        var newPath = NotePath.Canonical(Path.Combine(_root, "renamed.md"));
        File.Exists(newPath).ShouldBeTrue();
        _indexStore.Load().Notes.ShouldContainKey(newPath);
        manager.OpenPaths.ShouldContain(newPath);
    }

    [Fact]
    public void A_rename_onto_an_existing_name_is_refused_without_losing_either_file()
    {
        var oldPath = WriteNote("old.md", "# old");
        var taken = WriteNote("taken.md", "# taken");
        var index = new NoteIndex();
        index.Notes[oldPath] = StateAt(10, 10);

        var manager = Build(index);
        manager.RestoreOpenNotes();

        _factory.For(oldPath).RaiseRename("taken");

        File.ReadAllText(oldPath).ShouldBe("# old");
        File.ReadAllText(taken).ShouldBe("# taken");
        manager.OpenPaths.ShouldContain(oldPath);
    }

    // ---- Link-driven opening -----------------------------------------------

    [Fact]
    public void A_relative_md_link_opens_a_second_window()
    {
        var first = WriteNote("first.md");
        var second = WriteNote("second.md");

        var manager = Build();
        manager.OpenNote(first);

        _factory.For(first).RaiseOpenNote(second);

        _factory.Created.Count.ShouldBe(2);
        manager.OpenPaths.ShouldContain(second);
    }

    [Fact]
    public void A_new_note_is_created_open_and_marked_open()
    {
        var manager = Build();

        var path = manager.CreateAndOpenNote();

        File.Exists(path).ShouldBeTrue();
        NotePath.IsCanonical(path).ShouldBeTrue();
        _indexStore.Load().Notes[path].IsOpen.ShouldBeTrue();
        _factory.For(path).IsVisible.ShouldBeTrue();
    }

    // ---- Recovery offering (Step 6) ---------------------------------------

    [Fact]
    public void A_surviving_recovery_snapshot_is_offered_to_the_window()
    {
        var path = WriteNote("n.md", "# on disk");
        var recovery = new RecoveryStore(Path.Combine(_root, "recovery"));
        recovery.Save(new RecoveryEnvelope(
            path, "# unsaved", DateTime.UtcNow, "STALEHASH"));

        var manager = Build();
        manager.OpenNote(path);

        _factory.For(path).RecoveryOffered?.Content.ShouldBe("# unsaved");
    }

    [Fact]
    public void A_snapshot_that_already_matches_disk_is_cleared_not_offered()
    {
        // RecoveryStore.Clear is best-effort, so a snapshot can outlive its
        // successful save. Offering it would present older text as a recovery.
        var path = WriteNote("n.md", "# same");
        var recovery = new RecoveryStore(Path.Combine(_root, "recovery"));
        var hash = NoteFile.Read(path).ContentHash;
        recovery.Save(new RecoveryEnvelope(path, "# same", DateTime.UtcNow, hash));

        var manager = Build();
        manager.OpenNote(path);

        _factory.For(path).RecoveryOffered.ShouldBeNull();
        recovery.TryLoad(path).ShouldBeNull();
    }
}
