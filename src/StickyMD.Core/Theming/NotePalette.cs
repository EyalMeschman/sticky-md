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
        // Light: ContentBg is the big tinted surface, ChromeBg one step deeper,
        // CodeBg and Border the two steps between them. Dark: the same ladder
        // upside down, with enough chroma left in the surface that a note reads
        // as its COLOUR and not as another shade of near-black -- the first dark
        // palette here was mud, because it dropped saturation to reach darkness
        // instead of dropping lightness and keeping the hue.
        (NoteColor.Yellow, ThemeMode.Light) => new("#FCEE9B", "#40361B", "#E9D680", "#FFFAD6", "#40361B", "#9A6E12", "#F8EEB8", "#8A7A4E"),
        (NoteColor.Yellow, ThemeMode.Dark) => new("#362A13", "#F5E9C6", "#63501F", "#453619", "#F5E9C6", "#EDC55F", "#51411F", "#B0A077"),

        (NoteColor.Green, ThemeMode.Light) => new("#C6E6CE", "#1F3A2B", "#A7D3B4", "#E6F5EA", "#1F3A2B", "#1E7A50", "#D7EDDE", "#5E7F6C"),
        (NoteColor.Green, ThemeMode.Dark) => new("#182D20", "#D9EFE1", "#2F5740", "#1F3A2A", "#D9EFE1", "#6ED79B", "#26452F", "#8CB29C"),

        (NoteColor.Blue, ThemeMode.Light) => new("#C6DCF2", "#1C3149", "#A6C6E6", "#E5F0FB", "#1C3149", "#1667BC", "#D6E7F7", "#5C7692"),
        (NoteColor.Blue, ThemeMode.Dark) => new("#15263F", "#D8E7F7", "#2C4A73", "#1C3050", "#D8E7F7", "#7CBBF5", "#233A5D", "#8DA7C4"),

        (NoteColor.Pink, ThemeMode.Light) => new("#F7CEDA", "#44202D", "#EDB0C1", "#FDE8EE", "#44202D", "#C03965", "#F8DBE3", "#88626F"),
        (NoteColor.Pink, ThemeMode.Dark) => new("#351826", "#F5DDE6", "#613049", "#431F2F", "#F5DDE6", "#F28CB1", "#4F2639", "#BC93A4"),

        (NoteColor.Purple, ThemeMode.Light) => new("#D9CDF2", "#2E2350", "#C0AFE6", "#EEE7FA", "#2E2350", "#6B44C6", "#E4DCF6", "#6D6191"),
        (NoteColor.Purple, ThemeMode.Dark) => new("#241B40", "#E5DBF9", "#453578", "#2E2352", "#E5DBF9", "#AC8DF7", "#392B63", "#9F94C2"),

        (NoteColor.Gray, ThemeMode.Light) => new("#D8DEE5", "#20272F", "#BBC5D0", "#EFF2F6", "#20272F", "#3F6588", "#E2E7ED", "#64717E"),
        (NoteColor.Gray, ThemeMode.Dark) => new("#1D2228", "#DFE5EC", "#3A424B", "#262C33", "#DFE5EC", "#8FADC6", "#2F363E", "#929CA7"),

        // Charcoal is a dark note by design, so its two variants are close.
        (NoteColor.Charcoal, ThemeMode.Light) => new("#23272E", "#E6EAF0", "#3C434D", "#2D323A", "#E6EAF0", "#7FB6E0", "#373D46", "#99A3AF"),
        (NoteColor.Charcoal, ThemeMode.Dark) => new("#141619", "#E4E9EF", "#2C3037", "#1C1F23", "#E4E9EF", "#7FB6E0", "#24282D", "#98A2AC"),

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
