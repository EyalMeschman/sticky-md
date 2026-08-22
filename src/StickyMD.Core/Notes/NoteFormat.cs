namespace StickyMD.Core.Notes;

public enum NoteEncoding { Utf8NoBom, Utf8Bom, Utf16Le, Utf16Be }

public enum NoteNewline { Crlf, Lf }

/// <summary>
/// How a note is physically stored. Read from an existing file and reproduced
/// on write, so StickyMD never silently reformats a user's file.
/// </summary>
public sealed record NoteFormat(
    NoteEncoding Encoding,
    NoteNewline Newline,
    bool TrailingNewline)
{
    /// <summary>The format StickyMD gives to files it creates itself.</summary>
    public static NoteFormat Canonical { get; } =
        new(NoteEncoding.Utf8NoBom, NoteNewline.Crlf, TrailingNewline: true);
}

/// <param name="Text">Note text, always normalized to '\n' newlines.</param>
/// <param name="Format">The on-disk format, to be reproduced on write.</param>
/// <param name="ContentHash">SHA-256 hex of the RAW bytes on disk.</param>
public sealed record NoteContent(string Text, NoteFormat Format, string ContentHash);
