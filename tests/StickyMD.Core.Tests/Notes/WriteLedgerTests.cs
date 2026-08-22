using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Notes;

public class WriteLedgerTests
{
    [Fact]
    public void Recognises_a_write_it_recorded()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();

        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        ledger.IsOwnWrite(path, outcome.Size, outcome.ContentHash).ShouldBeTrue();
    }

    [Fact]
    public void Rejects_a_different_hash()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();
        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        ledger.IsOwnWrite(path, outcome.Size, NoteFile.Sha256("different"u8.ToArray()))
            .ShouldBeFalse();
    }

    [Fact]
    public void Rejects_a_different_size()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();
        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        ledger.IsOwnWrite(path, outcome.Size + 1, outcome.ContentHash).ShouldBeFalse();
    }

    [Fact]
    public void Rejects_an_unknown_path()
    {
        new WriteLedger().IsOwnWrite(@"C:\never\seen.md", 5, "abc").ShouldBeFalse();
    }

    [Fact]
    public void A_changed_timestamp_does_not_break_recognition()
    {
        // The fingerprint stores the timestamp for diagnostics but must never
        // compare it -- that fragility is exactly what content hashing avoids.
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();
        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        File.SetLastWriteTimeUtc(path, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        ledger.IsOwnWrite(path, outcome.Size, outcome.ContentHash).ShouldBeTrue();
    }

    [Fact]
    public void Path_comparison_is_case_insensitive()
    {
        using var dir = new TempDir();
        var path = dir.File("Note.md");
        var ledger = new WriteLedger();
        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        ledger.IsOwnWrite(path.ToUpperInvariant(), outcome.Size, outcome.ContentHash)
            .ShouldBeTrue();
    }

    [Fact]
    public void Path_comparison_normalises_relative_segments()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();
        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        var awkward = Path.Combine(dir.Path, "sub", "..", "a.md");

        ledger.IsOwnWrite(awkward, outcome.Size, outcome.ContentHash).ShouldBeTrue();
    }

    [Fact]
    public void Recording_twice_keeps_only_the_latest()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();

        var first = NoteFile.AtomicWrite(path, "one", NoteFormat.Canonical);
        ledger.Record(path, first);
        var second = NoteFile.AtomicWrite(path, "two", NoteFormat.Canonical);
        ledger.Record(path, second);

        ledger.IsOwnWrite(path, second.Size, second.ContentHash).ShouldBeTrue();
        ledger.IsOwnWrite(path, first.Size, first.ContentHash).ShouldBeFalse();
    }

    [Fact]
    public void Peek_exposes_the_stored_fingerprint()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var ledger = new WriteLedger();
        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        var fingerprint = ledger.Peek(path).ShouldNotBeNull();

        fingerprint.Size.ShouldBe(outcome.Size);
        fingerprint.ContentHash.ShouldBe(outcome.ContentHash);
        fingerprint.LastWriteUtc.ShouldBe(outcome.LastWriteUtc);
        fingerprint.NormalizedPath.ShouldBe(WriteLedger.Normalize(path));
    }

    [Fact]
    public void Peek_returns_null_for_an_unknown_path()
        => new WriteLedger().Peek(@"C:\never\seen.md").ShouldBeNull();

    [Fact]
    public void One_notes_fingerprint_does_not_satisfy_another_note()
    {
        using var dir = new TempDir();
        var a = dir.File("a.md");
        var b = dir.File("b.md");
        var ledger = new WriteLedger();

        var outcomeA = NoteFile.AtomicWrite(a, "note A", NoteFormat.Canonical);
        var outcomeB = NoteFile.AtomicWrite(b, "note B", NoteFormat.Canonical);
        ledger.Record(a, outcomeA);
        ledger.Record(b, outcomeB);

        // Each path must be satisfied only by its own fingerprint.
        ledger.IsOwnWrite(a, outcomeA.Size, outcomeA.ContentHash).ShouldBeTrue();
        ledger.IsOwnWrite(b, outcomeB.Size, outcomeB.ContentHash).ShouldBeTrue();
        ledger.IsOwnWrite(a, outcomeB.Size, outcomeB.ContentHash).ShouldBeFalse();
        ledger.IsOwnWrite(b, outcomeA.Size, outcomeA.ContentHash).ShouldBeFalse();
    }

    [Fact]
    public void The_ledger_is_usable_through_its_interface()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        // Task 12's NoteWatcher takes IWriteLedger, not WriteLedger.
        IWriteLedger ledger = new WriteLedger();

        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);
        ledger.Record(path, outcome);

        ledger.IsOwnWrite(path, outcome.Size, outcome.ContentHash).ShouldBeTrue();
        ledger.Peek(path).ShouldNotBeNull();
    }
}
