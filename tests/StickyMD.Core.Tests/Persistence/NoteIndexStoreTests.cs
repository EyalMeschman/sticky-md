using Shouldly;
using StickyMD.Core.Persistence;
using StickyMD.Core.Tests.TestSupport;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Tests.Persistence;

public class NoteIndexStoreTests
{
    private static NoteState Sample => new(
        X: 1200, Y: 80, W: 320, H: 420,
        Monitor: @"\\.\DISPLAY2",
        Color: NoteColor.Blue,
        Opacity: 0.95,
        AlwaysOnTop: true,
        IsOpen: true,
        LastOpenedUtc: new DateTime(2026, 8, 22, 10, 14, 0, DateTimeKind.Utc));

    [Fact]
    public void Round_trips_every_field()
    {
        using var dir = new TempDir();
        var store = new NoteIndexStore(dir.File("notes.json"));
        var index = new NoteIndex();
        index.Notes[@"C:\notes\standup.md"] = Sample;

        store.Save(index);
        var loaded = store.Load();

        loaded.Version.ShouldBe(1);
        // Note: DateTime.Equals ignores Kind, so this assertion would still pass if a
        // future serialiser change mangled Utc into Unspecified. Verified correct today.
        loaded.Notes[@"C:\notes\standup.md"].ShouldBe(Sample);
    }

    [Fact]
    public void Missing_file_loads_an_empty_index()
    {
        using var dir = new TempDir();
        var store = new NoteIndexStore(dir.File("notes.json"));

        var loaded = store.Load();

        loaded.Notes.ShouldBeEmpty();
        loaded.Version.ShouldBe(1);
    }

    [Fact]
    public void Path_keys_are_case_insensitive()
    {
        using var dir = new TempDir();
        var store = new NoteIndexStore(dir.File("notes.json"));
        var index = new NoteIndex();
        index.Notes[@"C:\Notes\Standup.md"] = Sample;
        store.Save(index);

        var loaded = store.Load();

        loaded.Notes.ContainsKey(@"c:\notes\standup.md").ShouldBeTrue();
    }

    [Fact]
    public void Corrupt_json_is_backed_up_and_an_empty_index_returned()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new NoteIndexStore(path);

        var loaded = store.Load();

        loaded.Notes.ShouldBeEmpty();
        store.LastCorruptBackupPath.ShouldNotBeNull();
        File.Exists(store.LastCorruptBackupPath!).ShouldBeTrue();
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void An_unknown_version_is_moved_aside_and_labelled_by_version()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        File.WriteAllText(path, """{"version":99,"notes":{}}""");
        var store = new NoteIndexStore(path);

        var loaded = store.Load();

        loaded.Notes.ShouldBeEmpty();
        // Named for what actually happened -- not "corrupt", which it is not.
        store.LastCorruptBackupPath.ShouldBe(path + ".v99-newer-1");
        File.Exists(path + ".v99-newer-1").ShouldBeTrue();
    }

    [Fact]
    public void Corrupt_backups_get_increasing_suffixes()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");

        File.WriteAllText(path, "broken");
        new NoteIndexStore(path).Load();
        File.WriteAllText(path, "broken again");
        new NoteIndexStore(path).Load();

        File.Exists(path + ".corrupt-1").ShouldBeTrue();
        File.Exists(path + ".corrupt-2").ShouldBeTrue();
    }

    [Fact]
    public void Serialised_json_uses_the_property_names_from_the_spec()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        var store = new NoteIndexStore(path);
        var index = new NoteIndex();
        index.Notes[@"C:\notes\a.md"] = Sample;

        store.Save(index);
        var json = File.ReadAllText(path);

        json.ShouldContain("\"version\"");
        json.ShouldContain("\"notes\"");
        json.ShouldContain("\"alwaysOnTop\"");
        json.ShouldContain("\"lastOpenedUtc\"");
        json.ShouldContain("\"isOpen\"");
        json.ShouldContain("\"monitor\"");
    }

    [Fact]
    public void Color_is_serialised_as_a_name_not_a_number()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        var store = new NoteIndexStore(path);
        var index = new NoteIndex();
        index.Notes[@"C:\notes\a.md"] = Sample;

        store.Save(index);

        File.ReadAllText(path).ShouldContain("\"Blue\"");
    }

    [Fact]
    public void Save_creates_the_directory_if_needed()
    {
        using var dir = new TempDir();
        var nested = Path.Combine(dir.Path, "StickyMD", "notes.json");
        var store = new NoteIndexStore(nested);

        store.Save(new NoteIndex());

        File.Exists(nested).ShouldBeTrue();
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        var store = new NoteIndexStore(path);

        store.Save(new NoteIndex());

        Directory.GetFiles(dir.Path).ShouldBe([path]);
    }

    [Fact]
    public void Load_never_throws_for_an_empty_file()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        File.WriteAllBytes(path, []);

        Should.NotThrow(() => new NoteIndexStore(path).Load());
    }

    [Fact]
    public void A_json_null_for_notes_loads_an_empty_index_without_throwing()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        // Valid, current-version JSON. STJ overwrites the property initializer with
        // null, so this used to throw ArgumentNullException out of Load.
        File.WriteAllText(path, """{"version":1,"notes":null}""");

        var loaded = new NoteIndexStore(path).Load();

        loaded.Notes.ShouldBeEmpty();
        loaded.Version.ShouldBe(1);
    }

    [Fact]
    public void A_failed_backup_reports_no_path_rather_than_a_phantom_one()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        File.WriteAllText(path, "{ not json");
        var store = new NoteIndexStore(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Load must still not throw, and must not claim a backup it could not make.
            store.Load().Notes.ShouldBeEmpty();
            store.LastCorruptBackupPath.ShouldBeNull();
        }

        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void LastCorruptBackupPath_clears_on_a_subsequent_good_load()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new NoteIndexStore(path);

        store.Load();
        store.LastCorruptBackupPath.ShouldNotBeNull();

        var index = new NoteIndex();
        index.Notes[@"C:\notes\a.md"] = Sample;
        store.Save(index);
        store.Load();

        store.LastCorruptBackupPath.ShouldBeNull();
    }

    [Fact]
    public void A_note_with_no_monitor_round_trips()
    {
        using var dir = new TempDir();
        var store = new NoteIndexStore(dir.File("notes.json"));
        var index = new NoteIndex();
        index.Notes[@"C:\notes\a.md"] = Sample with { Monitor = null };

        store.Save(index);

        store.Load().Notes[@"C:\notes\a.md"].Monitor.ShouldBeNull();
    }
}
