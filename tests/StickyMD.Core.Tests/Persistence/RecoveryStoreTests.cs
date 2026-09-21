using Shouldly;
using StickyMD.Core.Persistence;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Persistence;

public class RecoveryStoreTests
{
    private static RecoveryEnvelope Envelope(string path, string content = "unsaved text")
        => new(path, content, new DateTime(2026, 8, 22, 10, 14, 0, DateTimeKind.Utc), "sha256:abc");

    [Fact]
    public void Round_trips_every_field()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);
        var envelope = Envelope(@"C:\notes\standup.md");

        store.Save(envelope);

        store.TryLoad(@"C:\notes\standup.md").ShouldBe(envelope);
        store.TryLoad(@"C:\notes\standup.md")!.CreatedUtc.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public void Saving_twice_keeps_one_snapshot_per_note()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);
        var path = @"C:\notes\a.md";

        store.Save(Envelope(path, "first"));
        store.Save(Envelope(path, "second"));

        store.TryLoad(path)!.Content.ShouldBe("second");
        Directory.GetFiles(dir.Path).Length.ShouldBe(1);
    }

    [Fact]
    public void Clear_removes_the_snapshot()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);
        var path = @"C:\notes\a.md";
        store.Save(Envelope(path));

        store.Clear(path);

        store.TryLoad(path).ShouldBeNull();
        Directory.GetFiles(dir.Path).ShouldBeEmpty();
    }

    [Fact]
    public void Clear_on_a_note_with_no_snapshot_is_harmless()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);

        Should.NotThrow(() => store.Clear(@"C:\notes\never-saved.md"));
    }

    [Fact]
    public void TryLoad_returns_null_for_an_unknown_note()
    {
        using var dir = new TempDir();

        new RecoveryStore(dir.Path).TryLoad(@"C:\notes\unknown.md").ShouldBeNull();
    }

    [Fact]
    public void FileNameFor_is_a_sha256_hex_json_file()
    {
        var name = RecoveryStore.FileNameFor(@"C:\notes\a.md");

        name.ShouldEndWith(".json");
        Path.GetFileNameWithoutExtension(name).Length.ShouldBe(64);
    }

    [Fact]
    public void FileNameFor_ignores_path_casing_and_relative_segments()
    {
        var a = RecoveryStore.FileNameFor(@"C:\notes\a.md");
        var b = RecoveryStore.FileNameFor(@"C:\NOTES\sub\..\A.MD");

        a.ShouldBe(b);
    }

    [Fact]
    public void Different_notes_get_different_files()
    {
        RecoveryStore.FileNameFor(@"C:\notes\a.md")
            .ShouldNotBe(RecoveryStore.FileNameFor(@"C:\notes\b.md"));
    }

    [Fact]
    public void Save_creates_the_recovery_directory()
    {
        using var dir = new TempDir();
        var nested = Path.Combine(dir.Path, "recovery");

        new RecoveryStore(nested).Save(Envelope(@"C:\notes\a.md"));

        Directory.Exists(nested).ShouldBeTrue();
    }

    [Fact]
    public void Clear_reports_failure_when_the_snapshot_cannot_be_deleted()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.Path);
        var notePath = @"C:\notes\a.md";
        store.Save(Envelope(notePath));
        var snapshot = Path.Combine(dir.Path, RecoveryStore.FileNameFor(notePath));

        // An exclusive handle blocks deletion. Clear must report the failure rather
        // than pretending the stale snapshot is gone.
        using (new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Clear(notePath).ShouldBeFalse();
        }

        store.Clear(notePath).ShouldBeTrue();
        store.TryLoad(notePath).ShouldBeNull();
    }
}
