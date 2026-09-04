namespace StickyMD.Core.Theming;

public enum NoteColor { Yellow, Green, Blue, Pink, Purple, Gray, Charcoal }

public enum ThemeMode { Light, Dark }

/// <param name="ChromeBg">Header bar background.</param>
/// <param name="ChromeFg">Header bar text and glyphs.</param>
/// <param name="Border">1px window border.</param>
/// <param name="ContentBg">Rendered/edited note body background.</param>
/// <param name="ContentFg">Body text.</param>
/// <param name="Accent">Links, checkbox ticks, focus rings.</param>
/// <param name="CodeBg">Inline code and fenced block background.</param>
/// <param name="Muted">Secondary text, rules, blockquote bars.</param>
/// <param name="Mode">
/// The light/dark mode this theme was built for. Carried on the theme itself
/// so a consumer that needs to re-derive a sibling theme -- the ⋯ menu's
/// colour switch -- cannot drift from the mode the window is actually
/// showing.
/// </param>
public sealed record NoteTheme(
    string ChromeBg,
    string ChromeFg,
    string Border,
    string ContentBg,
    string ContentFg,
    string Accent,
    string CodeBg,
    string Muted,
    ThemeMode Mode = ThemeMode.Light);

/// <summary>
/// The one place note colors are defined. WPF chrome and rendered HTML both
/// read from here so a note's window and its content can never disagree.
/// </summary>
public static class NotePalette
{
    public static IReadOnlyList<NoteColor> All { get; } = Enum.GetValues<NoteColor>();

    public static NoteTheme Get(NoteColor color, ThemeMode mode) => Base(color, mode) with { Mode = mode };

    private static NoteTheme Base(NoteColor color, ThemeMode mode) => (color, mode) switch
    {
        (NoteColor.Yellow, ThemeMode.Light) => new("#FCEE9B", "#3A3320", "#E8D77E", "#FFF7C0", "#3A3320", "#B08900", "#F5E9A8", "#857A50"),
        (NoteColor.Yellow, ThemeMode.Dark) => new("#2E2A19", "#F0E9C8", "#554E2E", "#3A3520", "#F0E9C8", "#E0C34A", "#464026", "#A79E78"),

        (NoteColor.Green, ThemeMode.Light) => new("#C9EDC4", "#23361F", "#A8DCA0", "#DFF5DC", "#23361F", "#2E7D32", "#D0EBCB", "#5E7458"),
        (NoteColor.Green, ThemeMode.Dark) => new("#1B2818", "#D6E9D2", "#35492F", "#22321F", "#D6E9D2", "#7CC47F", "#2A3B26", "#8FA88B"),

        (NoteColor.Blue, ThemeMode.Light) => new("#C4DDF5", "#1D2B38", "#9FC6E8", "#DCEBFA", "#1D2B38", "#1565C0", "#CFE2F5", "#566B7D"),
        (NoteColor.Blue, ThemeMode.Dark) => new("#17222B", "#D3E3F0", "#2F4150", "#1E2B36", "#D3E3F0", "#6FAEDB", "#26353F", "#8AA0B2"),

        (NoteColor.Pink, ThemeMode.Light) => new("#F5C8D9", "#3A2029", "#E8A3BD", "#FBE0EA", "#3A2029", "#C2185B", "#F5D2E0", "#7D5665"),
        (NoteColor.Pink, ThemeMode.Dark) => new("#2B1920", "#F0D8E2", "#4D2F3A", "#362028", "#F0D8E2", "#E086AC", "#402631", "#B08D9C"),

        (NoteColor.Purple, ThemeMode.Light) => new("#DACCF0", "#2B2138", "#BFA9E0", "#EBE2F7", "#2B2138", "#6A3FB5", "#E0D4F2", "#6B5C85"),
        (NoteColor.Purple, ThemeMode.Dark) => new("#211B2B", "#E1D6F0", "#3E3350", "#2B2338", "#E1D6F0", "#A886DB", "#332A42", "#9C8CB2"),

        (NoteColor.Gray, ThemeMode.Light) => new("#DCE0E4", "#23282D", "#BFC6CC", "#ECEEF0", "#23282D", "#455A64", "#E0E4E8", "#667079"),
        (NoteColor.Gray, ThemeMode.Dark) => new("#1C1F22", "#DDE2E6", "#383D42", "#24282C", "#DDE2E6", "#8FA8B5", "#2C3135", "#949CA3"),

        // Charcoal is a dark note by design, so its two variants are close.
        (NoteColor.Charcoal, ThemeMode.Light) => new("#24272C", "#E4E7EA", "#454A52", "#2E3238", "#E4E7EA", "#7FB3D5", "#383D44", "#9AA3AC"),
        (NoteColor.Charcoal, ThemeMode.Dark) => new("#1A1C1F", "#E4E7EA", "#383C42", "#23262A", "#E4E7EA", "#7FB3D5", "#2C3035", "#9AA3AC"),

        _ => throw new ArgumentOutOfRangeException(nameof(color), color, "Unmapped note color."),
    };

    public static IReadOnlyDictionary<string, string> ToCssVariables(NoteTheme theme)
        => new Dictionary<string, string>
        {
            ["--note-chrome-bg"] = theme.ChromeBg,
            ["--note-chrome-fg"] = theme.ChromeFg,
            ["--note-border"] = theme.Border,
            ["--note-content-bg"] = theme.ContentBg,
            ["--note-content-fg"] = theme.ContentFg,
            ["--note-accent"] = theme.Accent,
            ["--note-code-bg"] = theme.CodeBg,
            ["--note-muted"] = theme.Muted,
        };
}
