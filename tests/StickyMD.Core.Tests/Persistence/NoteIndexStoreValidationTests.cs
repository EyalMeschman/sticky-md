using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Persistence;
using StickyMD.Core.Theming;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Persistence;

public class NoteIndexStoreValidationTests
{
    private static NoteIndexStore StoreWith(TempDir dir, string json)
        => new(dir.WriteFile("notes.json", json));

    [Fact]
    public void An_out_of_range_color_number_loads_and_is_defaulted()
    {
        using var dir = new TempDir();
        var store = StoreWith(dir, """
        {
          "version": 1,
          "notes": {
            "C:\\Notes\\a.md": {
              "x": 10, "y": 10, "w": 300, "h": 340,
              "monitor": null, "color": 99, "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-09-01T10:00:00Z"
            }
          }
        }
        """);

        var index = store.Load();

        index.Notes.Count.ShouldBe(1);
        var state = index.Notes.Values.Single();
        Enum.IsDefined(state.Color).ShouldBeTrue();
        store.LastLoadIssues.ShouldContain(i => i.Field == "color");
        store.LastCorruptBackupPath.ShouldBeNull("one bad value is not a corrupt file");
    }

    [Fact]
    public void One_unparseable_entry_costs_only_that_entry()
    {
        using var dir = new TempDir();
        var store = StoreWith(dir, """
        {
          "version": 1,
          "notes": {
            "C:\\Notes\\good.md": {
              "x": 10, "y": 10, "w": 300, "h": 340,
              "color": "blue", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-09-01T10:00:00Z"
            },
            "C:\\Notes\\bad.md": {
              "x": 10, "y": 10, "w": 300, "h": 340,
              "color": "banana", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-09-01T10:00:00Z"
            }
          }
        }
        """);

        var index = store.Load();

        // Before per-entry deserialization, the unknown enum STRING threw
        // JsonException, the whole file went to notes.json.corrupt-1, and every
        // note on the desktop lost its position. One bad value must cost one
        // note's geometry.
        index.Notes.Count.ShouldBe(1);
        index.Notes.Keys.Single().ShouldEndWith("good.md");
        store.LastLoadIssues.ShouldContain(i => i.Scope.EndsWith("bad.md"));
        store.LastCorruptBackupPath.ShouldBeNull();
    }

    [Fact]
    public void Keys_are_re_keyed_to_canonical_form()
    {
        using var dir = new TempDir();
        var store = StoreWith(dir, """
        {
          "version": 1,
          "notes": {
            "C:\\Notes\\sub\\..\\a.md": {
              "x": 10, "y": 10, "w": 300, "h": 340,
              "color": "blue", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-09-01T10:00:00Z"
            }
          }
        }
        """);

        var index = store.Load();

        var key = index.Notes.Keys.Single();
        NotePath.IsCanonical(key).ShouldBeTrue(key);
        key.ShouldBe(@"C:\Notes\a.md");
    }

    [Fact]
    public void An_unusable_key_is_dropped_with_an_issue_rather_than_throwing()
    {
        using var dir = new TempDir();
        var store = StoreWith(dir, """
        {
          "version": 1,
          "notes": {
            "": {
              "x": 10, "y": 10, "w": 300, "h": 340,
              "color": "blue", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-09-01T10:00:00Z"
            }
          }
        }
        """);

        var index = store.Load();

        index.Notes.ShouldBeEmpty();
        store.LastLoadIssues.ShouldNotBeEmpty();
    }

    [Fact]
    public void Two_keys_differing_only_in_case_keep_the_more_recently_opened_one()
    {
        using var dir = new TempDir();
        var store = StoreWith(dir, """
        {
          "version": 1,
          "notes": {
            "C:\\Notes\\a.md": {
              "x": 1, "y": 1, "w": 300, "h": 340,
              "color": "blue", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-01-01T10:00:00Z"
            },
            "C:\\NOTES\\A.MD": {
              "x": 2, "y": 2, "w": 300, "h": 340,
              "color": "green", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-09-01T10:00:00Z"
            }
          }
        }
        """);

        var index = store.Load();

        // Last-wins would depend on JSON document order, which nothing
        // guarantees. Newest-lastOpenedUtc-wins is deterministic and is also
        // the entry the user last actually used.
        index.Notes.Count.ShouldBe(1);
        index.Notes.Values.Single().Color.ShouldBe(NoteColor.Green);
        store.LastLoadIssues.ShouldNotBeEmpty();
    }

    [Fact]
    public void An_exact_lastOpenedUtc_tie_breaks_on_the_ordinally_smaller_key()
    {
        using var dir = new TempDir();
        // Same lastOpenedUtc on both -- newest-wins cannot decide this one, so
        // it must fall to a second, still-deterministic rule rather than to
        // JSON document order (which nothing guarantees). Ordinal comparison
        // ranks uppercase letters below lowercase ones, and "C:\Notes\a.md"
        // and "C:\NOTES\A.MD" first differ at the 5th character ('o' vs 'O'),
        // so "C:\NOTES\A.MD" is the ordinally smaller key and must win --
        // regardless of which of the two appears first in the file.
        var store = StoreWith(dir, """
        {
          "version": 1,
          "notes": {
            "C:\\Notes\\a.md": {
              "x": 1, "y": 1, "w": 300, "h": 340,
              "color": "blue", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-01-01T10:00:00Z"
            },
            "C:\\NOTES\\A.MD": {
              "x": 2, "y": 2, "w": 300, "h": 340,
              "color": "green", "opacity": 1.0,
              "alwaysOnTop": false, "isOpen": true,
              "lastOpenedUtc": "2026-01-01T10:00:00Z"
            }
          }
        }
        """);

        var index = store.Load();

        index.Notes.Count.ShouldBe(1);
        index.Notes.Keys.Single().ShouldBe(@"C:\NOTES\A.MD");
        index.Notes.Values.Single().Color.ShouldBe(NoteColor.Green);
        store.LastLoadIssues.ShouldContain(i =>
            i.Field == "key" && i.Detail.Contains(@"kept 'C:\NOTES\A.MD'"));
    }

    [Fact]
    public void A_genuinely_unparseable_file_still_gets_backed_up()
    {
        using var dir = new TempDir();
        var store = StoreWith(dir, "{ this is not json");

        var index = store.Load();

        index.Notes.ShouldBeEmpty();
        store.LastCorruptBackupPath.ShouldNotBeNull();
    }

    [Fact]
    public void A_clean_file_reports_no_issues()
    {
        using var dir = new TempDir();
        var store = new NoteIndexStore(dir.File("notes.json"));

        var index = new NoteIndex();
        index.Notes[Path.Combine(dir.Path, "a.md")] = new NoteState(
            10, 10, 300, 340, null, NoteColor.Yellow, 1.0, false, true,
            new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));
        store.Save(index);

        var reloaded = store.Load();

        reloaded.Notes.Count.ShouldBe(1);
        store.LastLoadIssues.ShouldBeEmpty();
        store.LastCorruptBackupPath.ShouldBeNull();
    }

    [Fact]
    public void Settings_load_reports_its_issues_too()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("settings.json", """
        { "notesRoot": "relative\\path", "defaultOpacity": 0.0, "theme": "Dark" }
        """);

        var store = new SettingsStore(path);
        var settings = store.Load();

        settings.NotesRoot.ShouldBe(AppSettings.DefaultNotesRoot);
        settings.DefaultOpacity.ShouldBe(StateValidator.MinOpacity);
        settings.Theme.ShouldBe(ThemePreference.Dark);
        store.LastLoadIssues.Count.ShouldBe(2);
    }
}
