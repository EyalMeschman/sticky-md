using StickyMD.Core.Markdown;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Persistence;

/// <summary>
/// What the user asked for. Distinct from <see cref="ThemeMode"/>, which is the
/// resolved light-or-dark that the palette needs.
/// </summary>
public enum ThemePreference { Light, Dark, System }

/// <summary>
/// App-wide preferences. Deliberately contains NO launch-at-startup flag --
/// the HKCU Run key is the single source of truth for that.
/// </summary>
public sealed record AppSettings
{
    public static string DefaultNotesRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "StickyMD Notes");

    public string NotesRoot { get; init; } = DefaultNotesRoot;

    public NoteColor DefaultColor { get; init; } = NoteColor.Yellow;

    public double DefaultOpacity { get; init; } = 1.0;

    public int DefaultWidth { get; init; } = 300;

    public int DefaultHeight { get; init; } = 340;

    /// <summary>
    /// The text size a NEW note starts at. Existing notes keep their own,
    /// which lives in <c>notes.json</c>.
    /// </summary>
    /// <remarks>
    /// Takes its value from <see cref="HtmlDocumentBuilder.DefaultFontSizePx"/>
    /// rather than repeating the number, so the shell's CSS and this cannot
    /// drift apart.
    /// </remarks>
    public int DefaultFontSizePx { get; init; } = HtmlDocumentBuilder.DefaultFontSizePx;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public string NewNoteHotkey { get; init; } = "Ctrl+Alt+N";

    public string ShowHideHotkey { get; init; } = "Ctrl+Alt+S";

    public bool AllowRemoteImages { get; init; }
}
