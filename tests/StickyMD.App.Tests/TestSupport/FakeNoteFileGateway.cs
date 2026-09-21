using System.IO;
using StickyMD.App.Services;
using StickyMD.Core.Notes;

namespace StickyMD.App.Tests.TestSupport;

/// <summary>
/// A note file in memory, with scriptable failures. Lets the retry schedule be
/// tested without a real lock, a real antivirus, or a real OneDrive.
/// </summary>
public sealed class FakeNoteFileGateway : INoteFileGateway
{
    private readonly Queue<Exception> _writeFailures = new();

    public string Content { get; set; } = string.Empty;

    public NoteFormat Format { get; set; } = NoteFormat.Canonical;

    public int WriteAttempts { get; private set; }

    public List<string> WrittenTexts { get; } = [];

    public List<NoteFormat> WrittenFormats { get; } = [];

    /// <summary>Fail the next <paramref name="count"/> writes.</summary>
    public void FailNextWrites(int count, Exception? with = null)
    {
        for (var i = 0; i < count; i++)
            _writeFailures.Enqueue(with ?? new IOException("the file is locked"));
    }

    public NoteContent Read(string path)
        => new(Content, Format, NoteFile.Sha256(
            System.Text.Encoding.UTF8.GetBytes(Content)));

    public NoteFile.WriteOutcome Write(string path, string text, NoteFormat format)
    {
        WriteAttempts++;

        if (_writeFailures.Count > 0) throw _writeFailures.Dequeue();

        Content = text;
        WrittenTexts.Add(text);
        WrittenFormats.Add(format);

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return new NoteFile.WriteOutcome(bytes.LongLength, NoteFile.Sha256(bytes));
    }
}
