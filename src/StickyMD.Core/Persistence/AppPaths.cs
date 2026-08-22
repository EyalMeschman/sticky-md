namespace StickyMD.Core.Persistence;

/// <summary>
/// Where StickyMD keeps its own state. LOCAL app data, never roaming --
/// window coordinates and monitor placement are machine-specific.
/// </summary>
public static class AppPaths
{
    public static string AppData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StickyMD");

    public static string NoteIndexFile { get; } = Path.Combine(AppData, "notes.json");

    public static string SettingsFile { get; } = Path.Combine(AppData, "settings.json");

    public static string RecoveryDir { get; } = Path.Combine(AppData, "recovery");
}
