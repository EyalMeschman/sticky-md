namespace StickyMD.Core.Notes;

/// <remarks>
/// Size and hash only. The write's timestamp is deliberately not kept:
/// timestamps are unreliable across OneDrive, differing filesystems, and
/// metadata-touching tools, so nothing may ever compare one.
/// </remarks>
public sealed record WriteFingerprint(
    string NormalizedPath,
    long Size,
    string ContentHash);

public interface IWriteLedger
{
    void Record(string path, NoteFile.WriteOutcome outcome);

    /// <summary>
    /// True when the file's current size and content hash match StickyMD's own
    /// most recent write to that path.
    /// </summary>
    /// <param name="contentHash">
    /// A hex SHA-256 digest as produced by <see cref="NoteFile.Sha256"/>. Compared
    /// case-insensitively, so either casing is accepted.
    /// </param>
    bool IsOwnWrite(string path, long size, string contentHash);

    WriteFingerprint? Peek(string path);
}

/// <summary>
/// Remembers what StickyMD last wrote to each note so the file watcher can
/// tell its own echo from a genuine external edit.
/// </summary>
public sealed class WriteLedger : IWriteLedger
{
    private readonly Dictionary<string, WriteFingerprint> _entries =
        NotePath.NewMap<WriteFingerprint>();

    private readonly object _gate = new();

    public void Record(string path, NoteFile.WriteOutcome outcome)
    {
        var normalized = NotePath.Canonical(path);
        var fingerprint = new WriteFingerprint(normalized, outcome.Size, outcome.ContentHash);

        lock (_gate) _entries[normalized] = fingerprint;
    }

    public bool IsOwnWrite(string path, long size, string contentHash)
    {
        var fingerprint = Peek(path);
        return fingerprint is not null
            && fingerprint.Size == size
            && string.Equals(fingerprint.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase);
    }

    public WriteFingerprint? Peek(string path)
    {
        var normalized = NotePath.Canonical(path);
        lock (_gate) return _entries.GetValueOrDefault(normalized);
    }
}
