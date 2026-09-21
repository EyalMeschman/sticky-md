using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Notes;

/// <summary>
/// The invariant that resolves Plan A's open finding 1: every boundary that
/// hands out a note path hands out the SAME form, so a map keyed by one
/// component's path finds an entry stored under another's.
/// </summary>
public class PathIdentityTests
{
    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private static IClock Clock()
        => new FixedClock(new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void EnumerateRoot_returns_canonical_paths()
    {
        using var dir = new TempDir();
        dir.WriteFile("a.md", "a");
        dir.WriteFile("B.md", "b");

        // Deliberately hand the repository a NON-canonical root.
        var repo = new NoteRepository(
            Path.Combine(dir.Path, "sub", ".."), Clock());

        foreach (var path in repo.EnumerateRoot())
            NotePath.IsCanonical(path).ShouldBeTrue(path);
    }

    [Fact]
    public void NotesRoot_is_canonical_even_when_the_constructor_was_given_junk()
    {
        using var dir = new TempDir();

        var repo = new NoteRepository(
            Path.Combine(dir.Path, ".", "sub", ".."), Clock());

        repo.NotesRoot.ShouldBe(dir.Path.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void CreateNewRecorded_returns_a_canonical_path()
    {
        using var dir = new TempDir();
        var repo = new NoteRepository(Path.Combine(dir.Path, "x", ".."), Clock());

        var (path, _) = repo.CreateNewRecorded();

        NotePath.IsCanonical(path).ShouldBeTrue(path);
    }

    [Fact]
    public void Rename_returns_a_canonical_path()
    {
        using var dir = new TempDir();
        dir.WriteFile("old.md", "x");
        var repo = new NoteRepository(dir.Path, Clock());

        var renamed = repo.Rename(Path.Combine(dir.Path, ".", "old.md"), "new");

        NotePath.IsCanonical(renamed).ShouldBeTrue(renamed);
        renamed.ShouldBe(Path.Combine(dir.Path, "new.md"));
    }

    [Fact]
    public void Renaming_a_note_to_the_name_it_already_has_returns_a_canonical_path()
    {
        // The NO-OP branch, which the test above does not reach -- it takes the
        // real-rename path. The trap is that the no-op returns early, so a
        // caller passing a dot-laden or otherwise non-canonical path used to be
        // the one caller who could get its own spelling back and then key
        // WindowManager's open-notes map with it. The file must also still be
        // there: an early return that had touched the filesystem would be worse
        // than the wrong string.
        using var dir = new TempDir();
        dir.WriteFile("old.md", "x");
        var repo = new NoteRepository(dir.Path, Clock());

        var renamed = repo.Rename(Path.Combine(dir.Path, ".", "old.md"), "old");

        NotePath.IsCanonical(renamed).ShouldBeTrue(renamed);
        renamed.ShouldBe(Path.Combine(dir.Path, "old.md"));
        File.ReadAllText(renamed).ShouldBe("x");
    }

    [Fact]
    public void A_repository_path_and_a_watcher_path_land_on_the_same_map_entry()
    {
        using var dir = new TempDir();
        dir.WriteFile("standup.md", "x");
        var repo = new NoteRepository(dir.Path, Clock());

        var fromRepo = repo.EnumerateRoot()[0];

        // What NoteWatcher emits for the same file, reached through a different
        // spelling than the repository's.
        var fromWatcher = NotePath.Canonical(
            Path.Combine(dir.Path, "sub", "..", "STANDUP.MD"));

        var map = NotePath.NewMap<string>();
        map[fromRepo] = "window";

        map.ContainsKey(fromWatcher).ShouldBeTrue(
            $"repo gave '{fromRepo}', watcher gave '{fromWatcher}'");
    }

    [Fact]
    public void The_write_ledger_suppresses_a_write_looked_up_by_a_different_spelling()
    {
        using var dir = new TempDir();
        var ledger = new WriteLedger();

        var written = Path.Combine(dir.Path, "Note.md");
        var outcome = NoteFile.AtomicWrite(written, "hello", NoteFormat.Canonical);
        ledger.Record(written, outcome);

        var lookedUpAs = Path.Combine(dir.Path, "sub", "..", "note.MD");

        ledger.IsOwnWrite(lookedUpAs, outcome.Size, outcome.ContentHash)
            .ShouldBeTrue();
    }

    [Fact]
    public void A_recovery_snapshot_is_found_through_a_different_spelling()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);

        var saved = Path.Combine(dir.Path, "Standup.md");
        store.Save(new RecoveryEnvelope(
            saved, "unsaved", new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc), "hash"));

        var lookedUpAs = Path.Combine(dir.Path, ".", "standup.MD");

        store.TryLoad(lookedUpAs).ShouldNotBeNull();
    }
}
