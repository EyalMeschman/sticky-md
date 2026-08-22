namespace StickyMD.Core.Persistence;

/// <summary>
/// Loads and saves notes.json. Load never throws: an unusable file is moved
/// aside and an empty index returned. The .md files are never touched, so the
/// worst case is losing window geometry.
/// </summary>
public sealed class NoteIndexStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public string? LastCorruptBackupPath { get; private set; }

    public NoteIndex Load()
    {
        LastCorruptBackupPath = null;

        if (!File.Exists(FilePath)) return new NoteIndex();

        var index = JsonFile.TryRead<NoteIndex>(FilePath);

        if (index is null)
        {
            LastCorruptBackupPath = JsonFile.BackupCorrupt(FilePath, "corrupt");
            return new NoteIndex();
        }

        if (index.Version != NoteIndex.CurrentVersion)
        {
            // Not corrupt -- just a schema this build does not understand. Never
            // half-parse it, but do not label the user's file as damaged either.
            var direction = index.Version > NoteIndex.CurrentVersion ? "newer" : "older";
            LastCorruptBackupPath =
                JsonFile.BackupCorrupt(FilePath, $"v{index.Version}-{direction}");
            return new NoteIndex();
        }

        // A case-insensitive copy constructor throws if the source holds two keys
        // differing only in case -- and System.Text.Json builds it with the ORDINAL
        // comparer, so it can. Last-wins keeps Load's never-throws contract.
        var notes = new Dictionary<string, NoteState>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in index.Notes ?? [])
            notes[entry.Key] = entry.Value;

        index.Notes = notes;

        return index;
    }

    public void Save(NoteIndex index) => JsonFile.Write(FilePath, index);
}
