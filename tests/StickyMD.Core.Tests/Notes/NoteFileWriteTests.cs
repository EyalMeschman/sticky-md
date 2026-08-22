using System.Text;
using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Notes;

public class NoteFileWriteTests
{
    [Fact]
    public void Round_trips_utf8_no_bom_with_lf()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "a\nb\n"u8.ToArray());
        var original = NoteFile.Read(path);

        NoteFile.AtomicWrite(path, original.Text, original.Format);

        File.ReadAllBytes(path).ShouldBe("a\nb\n"u8.ToArray());
    }

    [Fact]
    public void Round_trips_utf8_with_bom_and_crlf()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var expected = File.ReadAllBytes(WriteWith(path, "a\r\nb\r\n", new UTF8Encoding(true)));
        var original = NoteFile.Read(path);

        NoteFile.AtomicWrite(path, original.Text, original.Format);

        File.ReadAllBytes(path).ShouldBe(expected);
    }

    [Fact]
    public void Round_trips_utf16_le()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var expected = File.ReadAllBytes(
            WriteWith(path, "a\r\nb\r\n", new UnicodeEncoding(false, true)));
        var original = NoteFile.Read(path);

        NoteFile.AtomicWrite(path, original.Text, original.Format);

        File.ReadAllBytes(path).ShouldBe(expected);
    }

    [Fact]
    public void Round_trips_utf16_be()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var expected = File.ReadAllBytes(
            WriteWith(path, "a\nb\n", new UnicodeEncoding(true, true)));
        var original = NoteFile.Read(path);

        NoteFile.AtomicWrite(path, original.Text, original.Format);

        File.ReadAllBytes(path).ShouldBe(expected);
    }

    [Fact]
    public void Preserves_a_missing_trailing_newline()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "a\nb"u8.ToArray());
        var original = NoteFile.Read(path);

        NoteFile.AtomicWrite(path, original.Text, original.Format);

        File.ReadAllText(path).ShouldBe("a\nb");
    }

    [Fact]
    public void Adds_a_trailing_newline_when_the_format_says_so()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");

        NoteFile.AtomicWrite(path, "a\nb", NoteFormat.Canonical);

        File.ReadAllBytes(path).ShouldBe("a\r\nb\r\n"u8.ToArray());
    }

    [Fact]
    public void Does_not_double_the_trailing_newline()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");

        NoteFile.AtomicWrite(path, "a\n", NoteFormat.Canonical);

        File.ReadAllBytes(path).ShouldBe("a\r\n"u8.ToArray());
    }

    [Fact]
    public void New_files_get_the_canonical_format()
    {
        using var dir = new TempDir();
        var path = dir.File("new.md");

        NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);

        var bytes = File.ReadAllBytes(path);
        bytes.ShouldBe("hello\r\n"u8.ToArray());
        bytes[0].ShouldNotBe((byte)0xEF); // no BOM
    }

    [Fact]
    public void Outcome_reports_size_hash_and_timestamp()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");

        var outcome = NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);

        var bytes = File.ReadAllBytes(path);
        outcome.Size.ShouldBe(bytes.Length);
        outcome.ContentHash.ShouldBe(NoteFile.Sha256(bytes));
        outcome.ContentHash.ShouldBe(NoteFile.Read(path).ContentHash);
        outcome.LastWriteUtc.ShouldBe(File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Leaves_no_temp_file_behind_on_success()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");

        NoteFile.AtomicWrite(path, "hello", NoteFormat.Canonical);

        Directory.GetFiles(dir.Path).ShouldBe([path]);
    }

    [Fact]
    public void A_failed_write_leaves_the_original_file_intact()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "ORIGINAL"u8.ToArray());
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            Should.Throw<Exception>(
                () => NoteFile.AtomicWrite(path, "REPLACEMENT", NoteFormat.Canonical));

            File.ReadAllText(path).ShouldBe("ORIGINAL");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_failed_write_leaves_no_temp_file_behind()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "ORIGINAL"u8.ToArray());
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            Should.Throw<Exception>(
                () => NoteFile.AtomicWrite(path, "REPLACEMENT", NoteFormat.Canonical));

            Directory.GetFiles(dir.Path).ShouldBe([path]);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Overwrites_an_existing_file_completely_not_partially()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllText(path, "a very long original body that must not survive");

        NoteFile.AtomicWrite(path, "short", NoteFormat.Canonical);

        File.ReadAllText(path).ShouldBe("short\r\n");
    }

    [Fact]
    public void A_lone_surrogate_throws_rather_than_writing_a_replacement_character()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var original = "ORIGINAL"u8.ToArray();
        File.WriteAllBytes(path, original);

        // A Substring that splits a surrogate pair yields a legal .NET string with a
        // lone high surrogate. Encoding it leniently would persist U+FFFD, and the
        // outcome hash -- taken over those same bytes -- would agree with disk, so the
        // write ledger could never notice.
        Should.Throw<EncoderFallbackException>(
            () => NoteFile.AtomicWrite(path, "a\ud800b", NoteFormat.Canonical));

        File.ReadAllBytes(path).ShouldBe(original);
        Directory.GetFiles(dir.Path).ShouldBe([path]);
    }

    [Fact]
    public void Empty_text_under_the_canonical_format_is_a_two_byte_file()
    {
        using var dir = new TempDir();
        var path = dir.File("new.md");

        NoteFile.AtomicWrite(path, string.Empty, NoteFormat.Canonical);

        File.ReadAllBytes(path).ShouldBe("\r\n"u8.ToArray());
    }

    [Fact]
    public void A_failure_writing_the_temp_file_creates_nothing_at_all()
    {
        using var dir = new TempDir();
        // The parent directory does not exist, so the very first I/O call --
        // File.WriteAllBytes(temp, ...) -- fails before File.Replace or File.Move is
        // ever reached. This covers the earliest failure branch: nothing is created.
        var path = System.IO.Path.Combine(dir.Path, "nope", "a.md");

        Should.Throw<DirectoryNotFoundException>(
            () => NoteFile.AtomicWrite(path, "content", NoteFormat.Canonical));

        File.Exists(path).ShouldBeFalse();
        Directory.GetFiles(dir.Path, "*.stickymd-tmp", SearchOption.AllDirectories)
            .ShouldBeEmpty();
    }

    [Fact]
    public void A_failed_move_on_a_brand_new_note_leaves_no_temp_file_behind()
    {
        using var dir = new TempDir();
        // A directory at the target path: File.Exists is false for a directory, so
        // AtomicWrite takes the new-file branch and calls File.Move(temp, path) --
        // which then fails because the name is already taken. This is the only way to
        // reach that branch with the temp write having succeeded.
        var path = dir.File("a.md");
        Directory.CreateDirectory(path);

        Should.Throw<IOException>(
            () => NoteFile.AtomicWrite(path, "content", NoteFormat.Canonical));

        Directory.Exists(path).ShouldBeTrue();
        File.Exists(path).ShouldBeFalse();
        Directory.GetFiles(dir.Path, "*.stickymd-tmp").ShouldBeEmpty();
    }

    private static string WriteWith(string path, string content, Encoding encoding)
    {
        File.WriteAllText(path, content, encoding);
        return path;
    }
}
