using System.Globalization;
using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Notes;

public class NoteRepositoryTests
{
    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private static IClock On(int y, int m, int d)
        => new FixedClock(new DateTime(y, m, d, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void The_new_note_filename_date_does_not_follow_the_machine_calendar()
    {
        // Two things at once. "yyyy" resolves against the CURRENT CULTURE'S
        // calendar, so under ar-SA (Umm al-Qura) an unpinned format names
        // today's note 1448-xx-xx and it sorts nowhere near its neighbours --
        // the filename convention is user-visible and on-disk, so it must not
        // move with the machine's locale.
        //
        // This test also cannot RUN under <InvariantGlobalization>true</...>:
        // constructing a real culture throws CultureNotFoundException there.
        // That is deliberate. Invariant mode was set solution-wide once and it
        // crashed every WPF TextBox from inside a layout pass; if anyone puts
        // it back, this fails immediately instead of the app dying on the
        // first note it opens.
        using var dir = new TempDir();
        var repo = new NoteRepository(Path.Combine(dir.Path, "notes"), On(2026, 8, 22));

        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ar-SA");

        try
        {
            Path.GetFileName(repo.CreateNew()).ShouldBe("2026-08-22-untitled.md");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void EnsureRootExists_creates_the_directory()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "StickyMD Notes");
        var repo = new NoteRepository(root, On(2026, 8, 22));

        repo.EnsureRootExists();

        Directory.Exists(root).ShouldBeTrue();
    }

    [Fact]
    public void EnumerateRoot_on_a_missing_directory_returns_empty()
    {
        using var dir = new TempDir();
        var repo = new NoteRepository(Path.Combine(dir.Path, "nope"), On(2026, 8, 22));

        repo.EnumerateRoot().ShouldBeEmpty();
    }

    [Fact]
    public void EnumerateRoot_returns_only_markdown_files()
    {
        using var dir = new TempDir();
        dir.WriteFile("a.md", "a");
        dir.WriteFile("b.md", "b");
        dir.WriteFile("notes.txt", "not a note");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var notes = repo.EnumerateRoot();

        notes.Count.ShouldBe(2);
        notes.ShouldAllBe(p => p.EndsWith(".md"));
    }

    [Fact]
    public void EnumerateRoot_does_not_descend_into_subdirectories()
    {
        using var dir = new TempDir();
        dir.WriteFile("top.md", "top");
        var sub = Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        File.WriteAllText(Path.Combine(sub.FullName, "nested.md"), "nested");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        repo.EnumerateRoot().Count.ShouldBe(1);
    }

    [Fact]
    public void EnumerateRoot_returns_full_paths_in_a_stable_order()
    {
        using var dir = new TempDir();
        dir.WriteFile("b.md", "b");
        dir.WriteFile("a.md", "a");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var notes = repo.EnumerateRoot();

        notes[0].ShouldBe(Path.Combine(dir.Path, "a.md"));
        notes[1].ShouldBe(Path.Combine(dir.Path, "b.md"));
    }

    [Fact]
    public void CreateNew_uses_the_injected_date()
    {
        using var dir = new TempDir();
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var path = repo.CreateNew();

        Path.GetFileName(path).ShouldBe("2026-08-22-untitled.md");
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void CreateNew_suffixes_on_collision()
    {
        using var dir = new TempDir();
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        repo.CreateNew();
        var second = repo.CreateNew();
        var third = repo.CreateNew();

        Path.GetFileName(second).ShouldBe("2026-08-22-untitled-2.md");
        Path.GetFileName(third).ShouldBe("2026-08-22-untitled-3.md");
    }

    [Fact]
    public void CreateNew_creates_the_root_if_it_is_missing()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "StickyMD Notes");
        var repo = new NoteRepository(root, On(2026, 8, 22));

        var path = repo.CreateNew();

        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void CreateNew_writes_the_canonical_format()
    {
        using var dir = new TempDir();
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var path = repo.CreateNew();

        File.ReadAllBytes(path).ShouldBe("\r\n"u8.ToArray());
        NoteFile.Read(path).Format.ShouldBe(NoteFormat.Canonical);
    }

    [Fact]
    public void CreateNew_leaves_nothing_but_the_note_in_the_root()
    {
        // The notes root holds user content ONLY -- no temp files, no app state.
        using var dir = new TempDir();
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var path = repo.CreateNew();

        Directory.GetFiles(dir.Path).ShouldBe([path]);
    }

    [Fact]
    public void CreateNewRecorded_returns_an_outcome_that_matches_the_file()
    {
        using var dir = new TempDir();
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var (path, outcome) = repo.CreateNewRecorded();

        outcome.ContentHash.ShouldBe(NoteFile.Read(path).ContentHash);
        outcome.Size.ShouldBe(new FileInfo(path).Length);
    }

    [Fact]
    public void Rename_moves_the_file_and_returns_the_new_path()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("old.md", "body");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var renamed = repo.Rename(original, "standup.md");

        renamed.ShouldBe(Path.Combine(dir.Path, "standup.md"));
        File.Exists(original).ShouldBeFalse();
        File.ReadAllText(renamed).ShouldBe("body");
    }

    [Fact]
    public void Rename_appends_the_md_extension_when_omitted()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("old.md", "body");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        Path.GetFileName(repo.Rename(original, "standup")).ShouldBe("standup.md");
    }

    [Fact]
    public void Rename_rejects_a_path_separator()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("old.md", "body");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        Should.Throw<ArgumentException>(() => repo.Rename(original, @"sub\standup.md"));
        Should.Throw<ArgumentException>(() => repo.Rename(original, "sub/standup.md"));
        File.Exists(original).ShouldBeTrue();
    }

    [Fact]
    public void Rename_rejects_invalid_filename_characters()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("old.md", "body");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        Should.Throw<ArgumentException>(() => repo.Rename(original, "sta:ndup.md"));
    }

    [Fact]
    public void Rename_rejects_an_empty_name()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("old.md", "body");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        Should.Throw<ArgumentException>(() => repo.Rename(original, "   "));
    }

    [Fact]
    public void Rename_refuses_to_overwrite_an_existing_note()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("old.md", "body");
        dir.WriteFile("taken.md", "someone else");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        Should.Throw<IOException>(() => repo.Rename(original, "taken.md"));

        File.ReadAllText(dir.File("taken.md")).ShouldBe("someone else");
        File.Exists(original).ShouldBeTrue();
    }

    [Fact]
    public void Rename_to_the_same_name_is_a_no_op()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("same.md", "body");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        repo.Rename(original, "same.md").ShouldBe(original);
        File.ReadAllText(original).ShouldBe("body");
    }

    [Fact]
    public void Rename_can_change_only_the_letter_case()
    {
        using var dir = new TempDir();
        var original = dir.WriteFile("meeting notes.md", "body");
        var repo = new NoteRepository(dir.Path, On(2026, 8, 22));

        var renamed = repo.Rename(original, "Meeting Notes.md");

        // A case-only change used to be swallowed as a no-op, returning a path while
        // changing nothing. The filesystem performs it, so the repository must too.
        Path.GetFileName(renamed).ShouldBe("Meeting Notes.md");
        Directory.GetFiles(dir.Path).ShouldHaveSingleItem();
        Path.GetFileName(Directory.GetFiles(dir.Path)[0]).ShouldBe("Meeting Notes.md");
        File.ReadAllText(renamed).ShouldBe("body");
    }
}
