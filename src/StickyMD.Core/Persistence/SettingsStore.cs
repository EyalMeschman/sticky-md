namespace StickyMD.Core.Persistence;

/// <summary>
/// Loads and saves settings.json. Load never throws; an unusable file is moved
/// aside and defaults returned.
/// </summary>
public sealed class SettingsStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public string? LastCorruptBackupPath { get; private set; }

    public AppSettings Load()
    {
        LastCorruptBackupPath = null;

        if (!File.Exists(FilePath)) return new AppSettings();

        var settings = JsonFile.TryRead<AppSettings>(FilePath);

        if (settings is not null)
        {
            // A JSON null overwrites a property initializer, so a hand-edited or
            // truncated file can leave a non-nullable string null at runtime. Fall
            // back to the defaults rather than handing callers a null the type
            // system says cannot exist. Mirrors NoteIndexStore's `Notes ?? []`.
            var defaults = new AppSettings();

            return settings with
            {
                NotesRoot = settings.NotesRoot ?? defaults.NotesRoot,
                NewNoteHotkey = settings.NewNoteHotkey ?? defaults.NewNoteHotkey,
                ShowHideHotkey = settings.ShowHideHotkey ?? defaults.ShowHideHotkey,
            };
        }

        LastCorruptBackupPath = JsonFile.BackupCorrupt(FilePath, "corrupt");
        return new AppSettings();
    }

    public void Save(AppSettings settings) => JsonFile.Write(FilePath, settings);
}
