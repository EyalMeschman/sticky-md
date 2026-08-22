using Shouldly;
using StickyMD.Core.Persistence;
using StickyMD.Core.Tests.TestSupport;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Tests.Persistence;

public class SettingsStoreTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        var defaults = new AppSettings();

        defaults.DefaultColor.ShouldBe(NoteColor.Yellow);
        defaults.DefaultOpacity.ShouldBe(1.0);
        defaults.DefaultWidth.ShouldBe(300);
        defaults.DefaultHeight.ShouldBe(340);
        defaults.AllowRemoteImages.ShouldBeFalse();
        defaults.Theme.ShouldBe(ThemePreference.System);
        defaults.NewNoteHotkey.ShouldBe("Ctrl+Alt+N");
        defaults.ShowHideHotkey.ShouldBe("Ctrl+Alt+S");
    }

    [Fact]
    public void Default_notes_root_is_under_the_user_profile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        AppSettings.DefaultNotesRoot.ShouldBe(Path.Combine(profile, "StickyMD Notes"));
        new AppSettings().NotesRoot.ShouldBe(AppSettings.DefaultNotesRoot);
    }

    [Fact]
    public void Round_trips_every_field()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var settings = new AppSettings
        {
            NotesRoot = @"D:\my notes",
            DefaultColor = NoteColor.Charcoal,
            DefaultOpacity = 0.8,
            DefaultWidth = 420,
            DefaultHeight = 500,
            Theme = ThemePreference.Dark,
            NewNoteHotkey = "Ctrl+Shift+N",
            ShowHideHotkey = "Ctrl+Shift+S",
            AllowRemoteImages = true,
        };

        store.Save(settings);

        store.Load().ShouldBe(settings);
    }

    [Fact]
    public void Missing_file_loads_defaults()
    {
        using var dir = new TempDir();

        new SettingsStore(dir.File("settings.json")).Load().ShouldBe(new AppSettings());
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_defaults_returned()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "not json at all {{{");
        var store = new SettingsStore(path);

        store.Load().ShouldBe(new AppSettings());

        store.LastCorruptBackupPath.ShouldNotBeNull();
        File.Exists(store.LastCorruptBackupPath!).ShouldBeTrue();
    }

    [Fact]
    public void Serialised_json_never_contains_launchAtStartup()
    {
        // The Run registry key is the single source of truth. Caching it here
        // would create two states to reconcile.
        using var dir = new TempDir();
        var path = dir.File("settings.json");

        new SettingsStore(path).Save(new AppSettings());

        File.ReadAllText(path).ShouldNotContain("launchAtStartup", Case.Insensitive);
    }

    [Fact]
    public void AppSettings_has_no_launch_at_startup_member()
    {
        typeof(AppSettings).GetProperties()
            .Select(p => p.Name)
            .ShouldNotContain(n => n.Contains("Startup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Theme_is_serialised_as_a_name()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");

        new SettingsStore(path).Save(new AppSettings { Theme = ThemePreference.Dark });

        File.ReadAllText(path).ShouldContain("\"Dark\"");
    }

    [Fact]
    public void Unknown_json_properties_are_ignored_rather_than_fatal()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """{"defaultWidth":444,"somethingFromTheFuture":true}""");

        var loaded = new SettingsStore(path).Load();

        loaded.DefaultWidth.ShouldBe(444);
        loaded.DefaultColor.ShouldBe(NoteColor.Yellow);
    }

    [Fact]
    public void A_failed_backup_reports_no_path_rather_than_a_phantom_one()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "not json at all {{{");
        var store = new SettingsStore(path);

        // An exclusive handle makes both the read and the rename fail. Load must still
        // return defaults without throwing, and must not claim a backup it never made.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Load().ShouldBe(new AppSettings());
            store.LastCorruptBackupPath.ShouldBeNull();
        }

        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void A_json_null_for_a_string_setting_falls_back_to_its_default()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        // Valid JSON. STJ overwrites the property initializer, so without a guard this
        // yields a null NotesRoot on a non-nullable property.
        File.WriteAllText(path, """{"notesRoot":null,"newNoteHotkey":null}""");

        var loaded = new SettingsStore(path).Load();

        loaded.NotesRoot.ShouldBe(AppSettings.DefaultNotesRoot);
        loaded.NewNoteHotkey.ShouldBe("Ctrl+Alt+N");
        // Not treated as corrupt -- a null field is recoverable, unlike unparseable JSON.
        new SettingsStore(path).LastCorruptBackupPath.ShouldBeNull();
    }
}
