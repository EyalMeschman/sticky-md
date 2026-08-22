using System.Text;
using Shouldly;
using StickyMD.Core.Notes;
using StickyMD.Core.Tests.TestSupport;

namespace StickyMD.Core.Tests.Notes;

public class NoteFileReadTests
{
    [Fact]
    public void Reads_utf8_without_bom_and_lf()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "# Hi\nbody\n"u8.ToArray());

        var note = NoteFile.Read(path);

        note.Text.ShouldBe("# Hi\nbody\n");
        note.Format.Encoding.ShouldBe(NoteEncoding.Utf8NoBom);
        note.Format.Newline.ShouldBe(NoteNewline.Lf);
        note.Format.TrailingNewline.ShouldBeTrue();
    }

    [Fact]
    public void Reads_utf8_with_bom()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllText(path, "# Hi\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var note = NoteFile.Read(path);

        note.Format.Encoding.ShouldBe(NoteEncoding.Utf8Bom);
        note.Format.Newline.ShouldBe(NoteNewline.Crlf);
        note.Format.TrailingNewline.ShouldBeTrue();
        note.Text.ShouldBe("# Hi\n");
        note.Text.ShouldNotStartWith("﻿");
    }

    [Fact]
    public void Reads_utf16_le()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllText(path, "# Hi\r\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        var note = NoteFile.Read(path);

        note.Format.Encoding.ShouldBe(NoteEncoding.Utf16Le);
        note.Format.Newline.ShouldBe(NoteNewline.Crlf);
        note.Format.TrailingNewline.ShouldBeTrue();
        note.Text.ShouldBe("# Hi\n");
    }

    [Fact]
    public void Reads_utf16_be()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllText(path, "# Hi\n", new UnicodeEncoding(bigEndian: true, byteOrderMark: true));

        var note = NoteFile.Read(path);

        note.Format.Encoding.ShouldBe(NoteEncoding.Utf16Be);
        note.Format.Newline.ShouldBe(NoteNewline.Lf);
        note.Format.TrailingNewline.ShouldBeTrue();
        note.Text.ShouldBe("# Hi\n");
    }

    [Fact]
    public void Detects_crlf_newlines()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "a\r\nb\r\n"u8.ToArray());

        var note = NoteFile.Read(path);

        note.Format.Newline.ShouldBe(NoteNewline.Crlf);
        note.Text.ShouldBe("a\nb\n");
    }

    [Fact]
    public void Mixed_newlines_resolve_to_the_dominant_convention()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "a\r\nb\r\nc\nd\r\n"u8.ToArray());

        NoteFile.Read(path).Format.Newline.ShouldBe(NoteNewline.Crlf);
    }

    [Fact]
    public void A_file_with_no_newlines_reports_the_canonical_convention()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "single line"u8.ToArray());

        var note = NoteFile.Read(path);

        note.Format.Newline.ShouldBe(NoteNewline.Crlf);
        note.Format.TrailingNewline.ShouldBeFalse();
    }

    [Fact]
    public void Detects_a_missing_trailing_newline()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "a\nb"u8.ToArray());

        NoteFile.Read(path).Format.TrailingNewline.ShouldBeFalse();
    }

    [Fact]
    public void An_empty_file_reads_as_empty_with_the_canonical_format()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, []);

        var note = NoteFile.Read(path);

        note.Text.ShouldBe("");
        note.Format.ShouldBe(NoteFormat.Canonical with { TrailingNewline = false });
    }

    [Fact]
    public void Hash_is_over_the_raw_bytes_and_is_stable()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllBytes(path, "hello"u8.ToArray());

        var first = NoteFile.Read(path);
        var second = NoteFile.Read(path);

        first.ContentHash.ShouldBe(second.ContentHash);
        first.ContentHash.Length.ShouldBe(64);
        first.ContentHash.ShouldBe(NoteFile.Sha256("hello"u8.ToArray()));
    }

    [Fact]
    public void Different_newline_conventions_produce_different_hashes()
    {
        using var dir = new TempDir();
        var lf = dir.File("lf.md");
        var crlf = dir.File("crlf.md");
        File.WriteAllBytes(lf, "a\nb"u8.ToArray());
        File.WriteAllBytes(crlf, "a\r\nb"u8.ToArray());

        NoteFile.Read(lf).ContentHash
            .ShouldNotBe(NoteFile.Read(crlf).ContentHash);
    }

    [Fact]
    public void Preserves_non_ascii_content()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        File.WriteAllText(path, "héllo — naïve 日本語\n", new UTF8Encoding(false));

        NoteFile.Read(path).Text.ShouldBe("héllo — naïve 日本語\n");
    }

    [Fact]
    public void A_file_that_is_not_valid_utf8_throws_rather_than_silently_corrupting_it()
    {
        using var dir = new TempDir();
        var path = dir.File("ansi.md");
        // "café" in Windows-1252: the trailing 0xE9 is not valid UTF-8. Decoding it
        // leniently would yield U+FFFD, and the next save would persist that in
        // place of the é -- silent, permanent data loss.
        File.WriteAllBytes(path, [0x63, 0x61, 0x66, 0xE9]);

        Should.Throw<DecoderFallbackException>(() => NoteFile.Read(path));

        // The file itself must be untouched.
        File.ReadAllBytes(path).ShouldBe(new byte[] { 0x63, 0x61, 0x66, 0xE9 });
    }

    [Fact]
    public void Read_does_not_modify_the_file_on_disk()
    {
        using var dir = new TempDir();
        var path = dir.File("a.md");
        var original = "# Hi\r\nbody"u8.ToArray();
        File.WriteAllBytes(path, original);
        var before = File.GetLastWriteTimeUtc(path);

        NoteFile.Read(path);

        File.ReadAllBytes(path).ShouldBe(original);
        File.GetLastWriteTimeUtc(path).ShouldBe(before);
    }
}
