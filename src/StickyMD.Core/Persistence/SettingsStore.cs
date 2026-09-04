namespace StickyMD.Core.Persistence;

/// <summary>
/// Loads and saves settings.json. Load never throws; an unusable file is moved
/// aside and defaults returned.
/// </summary>
public sealed class SettingsStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public string? LastCorruptBackupPath { get; private set; }

    /// <summary>
    /// Corrections made by the most recent <see cref="Load"/>. Empty after a
    /// clean load.
    /// </summary>
    public IReadOnlyList<ValidationIssue> LastLoadIssues { get; private set; } = [];

    public AppSettings Load()
    {
        LastCorruptBackupPath = null;
        var issues = new List<ValidationIssue>();
        LastLoadIssues = issues;

        if (!File.Exists(FilePath)) return new AppSettings();

        var settings = JsonFile.TryRead<AppSettings>(FilePath);

        if (settings is null)
        {
            LastCorruptBackupPath = JsonFile.BackupCorrupt(FilePath, "corrupt");
            return new AppSettings();
        }

        // A JSON null overwrites a property initializer, so a hand-edited or
        // truncated file can leave a non-nullable string null at runtime. Fill
        // those in BEFORE validating, or the validator dereferences a null the
        // type system says cannot exist.
        var defaults = new AppSettings();

        var filled = settings with
        {
            NotesRoot = settings.NotesRoot ?? defaults.NotesRoot,
            NewNoteHotkey = settings.NewNoteHotkey ?? defaults.NewNoteHotkey,
            ShowHideHotkey = settings.ShowHideHotkey ?? defaults.ShowHideHotkey,
        };

        return StateValidator.ValidateSettings(filled, issues);
    }

    public void Save(AppSettings settings) => JsonFile.Write(FilePath, settings);
}
