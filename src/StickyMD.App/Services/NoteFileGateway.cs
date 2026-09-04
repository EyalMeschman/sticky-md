using StickyMD.Core.Notes;

namespace StickyMD.App.Services;

/// <summary>
/// Note file I/O behind a seam, so <see cref="SaveCoordinator"/>'s retry
/// schedule can be tested against scripted failures rather than a real lock,
/// a real antivirus scan, or a real OneDrive.
/// </summary>
public interface INoteFileGateway
{
    NoteContent Read(string path);

    NoteFile.WriteOutcome Write(string path, string text, NoteFormat format);
}

public sealed class NoteFileGateway : INoteFileGateway
{
    public NoteContent Read(string path) => NoteFile.Read(path);

    public NoteFile.WriteOutcome Write(string path, string text, NoteFormat format)
        => NoteFile.AtomicWrite(path, text, format);
}
