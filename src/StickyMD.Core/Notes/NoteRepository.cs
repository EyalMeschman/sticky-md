namespace StickyMD.Core.Notes;

/// <summary>
/// The notes root as a collection of .md files. Nothing app-owned is ever
/// written here.
/// </summary>
/// <remarks>
/// Deletion is absent by design: sending a file to the Recycle Bin is platform
/// behavior and belongs in the App layer, keeping Core free of Win32.
/// </remarks>
public sealed class NoteRepository(string notesRoot, IClock clock)
{
    private const string UntitledStem = "untitled";
    private const int MaxCreateAttempts = 16;

    /// <summary>
    /// Canonical. Every path this class produces is built by combining onto
    /// this, which is what makes them canonical without a second pass.
    /// </summary>
    public string NotesRoot { get; } = NotePath.Canonical(notesRoot);

    public void EnsureRootExists() => Directory.CreateDirectory(NotesRoot);

    public IReadOnlyList<string> EnumerateRoot()
    {
        if (!Directory.Exists(NotesRoot)) return [];

        // Combining onto a canonical root already yields canonical paths, but
        // canonicalise explicitly anyway: this method is the boundary the rest
        // of the app trusts, and a filename the filesystem accepts while
        // GetFullPath rejects must cost that one entry, not the listing.
        var notes = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
            NotesRoot, "*.md", SearchOption.TopDirectoryOnly))
        {
            if (NotePath.TryCanonical(path, out var canonical))
                notes.Add(canonical);
        }

        notes.Sort(NotePath.Comparer);
        return notes;
    }

    /// <summary>
    /// Creates a new empty note named for today's date, suffixing -2, -3 and so on
    /// past collisions.
    /// </summary>
    /// <remarks>
    /// NOT THREAD-SAFE. The free-name scan and the write are separate steps, and
    /// AtomicWrite's Replace briefly makes the claimed name look absent to a
    /// concurrent scan -- so two threads can still return the same path. The
    /// FileMode.CreateNew claim below narrows that window and closes the
    /// cross-process case, but does not eliminate it.
    ///
    /// CALLERS MUST SERIALISE. Plan C's new-note hotkey runs on the UI thread,
    /// which satisfies this; anything that dispatches note creation to a thread
    /// pool must add its own lock.
    /// </remarks>
    public string CreateNew() => CreateNewRecorded().Path;

    /// <summary>
    /// Creates a new note and returns both its path and the write outcome, so the
    /// caller can record it in an <see cref="IWriteLedger"/>. Without that, the
    /// watcher reports the app's own new note as an external change.
    /// </summary>
    /// <inheritdoc cref="CreateNew"/>
    public (string Path, NoteFile.WriteOutcome Outcome) CreateNewRecorded()
    {
        EnsureRootExists();

        var date = clock.UtcNow.ToString("yyyy-MM-dd");

        for (var attempt = 0; attempt < MaxCreateAttempts; attempt++)
        {
            // Canonical by construction: NotesRoot is canonical and Combine only
            // appends a bare filename. No second normalisation pass needed.
            var path = Path.Combine(NotesRoot, $"{date}-{UntitledStem}.md");

            for (var n = 2; File.Exists(path); n++)
                path = Path.Combine(NotesRoot, $"{date}-{UntitledStem}-{n}.md");

            try
            {
                // Claim the name ATOMICALLY before writing. A File.Exists scan alone
                // is check-then-act: a concurrent caller can take the name in the
                // gap, and AtomicWrite would then re-check File.Exists, take its
                // Replace branch, and SUCCEED by overwriting -- so both callers
                // would be told they created the same path. FileMode.CreateNew
                // throws instead, which is what makes the retry meaningful.
                using var claim = new FileStream(
                    path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException)
            {
                continue; // Name taken between the scan and the claim; try the next.
            }

            // We own the name, so no other caller can contend for it or for its
            // temp file. AtomicWrite replaces our empty placeholder with the
            // canonical bytes, keeping the format in one place.
            var outcome = NoteFile.AtomicWrite(path, string.Empty, NoteFormat.Canonical);
            return (path, outcome);
        }

        throw new IOException(
            $"Could not create a new note in '{NotesRoot}' after {MaxCreateAttempts} attempts.");
    }

    /// <summary>
    /// Renames a note on disk. The caller is responsible for re-keying the
    /// index entry.
    /// </summary>
    public string Rename(string currentPath, string newFileName)
    {
        if (string.IsNullOrWhiteSpace(newFileName))
            throw new ArgumentException("A note name cannot be empty.", nameof(newFileName));

        var name = newFileName.Trim();

        if (name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException(
                "A note name cannot contain a path separator.", nameof(newFileName));

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException(
                "A note name contains characters Windows does not allow.", nameof(newFileName));

        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            name += ".md";

        var fullCurrent = NotePath.Canonical(currentPath);

        var directory = Path.GetDirectoryName(fullCurrent) ?? NotesRoot;
        var target = NotePath.Canonical(Path.Combine(directory, name));

        // Truly identical, case included: nothing to do. Return the CANONICAL
        // form, not the caller's spelling -- a caller that passed a relative or
        // dot-laden path must not get it back and then key a map with it.
        if (string.Equals(fullCurrent, target, StringComparison.Ordinal))
            return fullCurrent;

        // A case-only change is a real rename the filesystem supports. Skip the
        // overwrite guard in that case -- File.Exists(target) is true because the
        // target IS this file under a case-insensitive comparison.
        if (!string.Equals(fullCurrent, target, StringComparison.OrdinalIgnoreCase)
            && File.Exists(target))
        {
            throw new IOException($"A note named '{name}' already exists.");
        }

        File.Move(fullCurrent, target);
        return target;
    }
}
