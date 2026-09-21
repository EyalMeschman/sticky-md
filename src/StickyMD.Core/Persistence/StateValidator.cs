using StickyMD.Core.Input;
using StickyMD.Core.Notes;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Persistence;

/// <summary>
/// Clamps or defaults persisted values that deserialise cleanly but are not
/// usable, reporting every correction.
/// </summary>
/// <remarks>
/// Deserialization checks SHAPE, not VALUES. System.Text.Json's
/// JsonStringEnumConverter accepts numeric enum values and does not
/// range-check them, so {"color": 99} produced (NoteColor)99 and crashed
/// NotePalette.Get with ArgumentOutOfRangeException -- a crash on startup,
/// after a successful load, pointing at the theming code.
///
/// Every rule here is a clamp or a default, never a rejection. Losing a note's
/// remembered position is acceptable; refusing to open the note is not.
/// </remarks>
public static class StateValidator
{
    /// <summary>Narrowest width the header buttons still fit in.</summary>
    public const int MinNoteWidth = 160;

    /// <summary>Shortest height that leaves a usable body under the header.</summary>
    public const int MinNoteHeight = 120;

    /// <summary>Generous enough for an 8K display, small enough to be sane.</summary>
    public const int MaxNoteEdge = 8192;

    /// <summary>
    /// WindowPlacement computes Right = X + Width with int addition. A saved
    /// int.MaxValue would overflow to a negative Right, and the clamp would
    /// then place the note somewhere absurd instead of rejecting it.
    /// </summary>
    public const int MaxCoordinate = 65536;

    /// <summary>
    /// Below roughly this, a note is invisible and cannot be found with the
    /// mouse in order to be fixed. An unrecoverable UI state is a lost note.
    /// </summary>
    public const double MinOpacity = 0.20;

    public const double MaxOpacity = 1.0;

    /// <summary>
    /// Below this the text is unreadable, which is a note the user cannot use.
    /// </summary>
    public const int MinFontSizePx = 9;

    /// <summary>
    /// Above this a default-sized note fits about two words per line.
    /// </summary>
    public const int MaxFontSizePx = 40;

    public static NoteState ValidateNote(
        NoteState raw,
        AppSettings defaults,
        string scope,
        DateTime nowUtc,
        List<ValidationIssue> issues)
    {
        void Report(string field, string detail)
            => issues.Add(new ValidationIssue(scope, field, detail));

        var color = raw.Color;
        if (!Enum.IsDefined(color))
        {
            Report("color", $"'{(int)raw.Color}' is not a note colour; used {defaults.DefaultColor}.");
            color = defaults.DefaultColor;
        }

        var opacity = raw.Opacity;
        if (double.IsNaN(opacity) || double.IsInfinity(opacity))
        {
            // Math.Clamp(NaN, …) returns NaN, so the clamp below would let it
            // through -- and Window.Opacity = NaN throws at assignment.
            Report("opacity", $"'{raw.Opacity}' is not a number; used {defaults.DefaultOpacity}.");
            opacity = defaults.DefaultOpacity;
        }
        else if (opacity < MinOpacity || opacity > MaxOpacity)
        {
            var clamped = Math.Clamp(opacity, MinOpacity, MaxOpacity);
            Report("opacity", $"{opacity} is outside {MinOpacity}-{MaxOpacity}; used {clamped}.");
            opacity = clamped;
        }

        var width = ClampEdge(raw.W, defaults.DefaultWidth, MinNoteWidth, "w", Report);
        var height = ClampEdge(raw.H, defaults.DefaultHeight, MinNoteHeight, "h", Report);

        var x = ClampCoordinate(raw.X, "x", Report);
        var y = ClampCoordinate(raw.Y, "y", Report);

        var monitor = string.IsNullOrWhiteSpace(raw.Monitor) ? null : raw.Monitor;

        var fontSize = raw.FontSizePx;
        if (fontSize <= 0)
        {
            // SILENT, and deliberately. Zero is what a note written before
            // this field existed deserialises to, so this branch is an upgrade
            // rather than a correction -- reporting it would write one line per
            // note into diagnostics.log on the first run after an update, and
            // train the reader to skim past the corrections that matter.
            fontSize = defaults.DefaultFontSizePx;
        }
        else if (fontSize < MinFontSizePx || fontSize > MaxFontSizePx)
        {
            var clamped = Math.Clamp(fontSize, MinFontSizePx, MaxFontSizePx);
            Report("fontSizePx", $"{fontSize} is outside {MinFontSizePx}-{MaxFontSizePx}; used {clamped}.");
            fontSize = clamped;
        }

        var lastOpened = ToUtc(raw.LastOpenedUtc);
        if (lastOpened > nowUtc)
        {
            // A future timestamp would sort this note to the top of Recent
            // Notes permanently, and Recent means recently OPENED.
            Report("lastOpenedUtc", $"{lastOpened:O} is in the future; used now.");
            lastOpened = nowUtc;
        }

        return raw with
        {
            X = x,
            Y = y,
            W = width,
            H = height,
            Monitor = monitor,
            Color = color,
            Opacity = opacity,
            LastOpenedUtc = lastOpened,
            FontSizePx = fontSize,
        };
    }

    public static AppSettings ValidateSettings(
        AppSettings raw, List<ValidationIssue> issues)
    {
        const string scope = "settings.json";
        var defaults = new AppSettings();

        void Report(string field, string detail)
            => issues.Add(new ValidationIssue(scope, field, detail));

        var notesRoot = raw.NotesRoot;
        if (!Path.IsPathRooted(notesRoot)
            || !NotePath.TryCanonical(notesRoot, out notesRoot))
        {
            // A relative notes root would resolve against the process working
            // directory, which for a shortcut or a startup launch is arbitrary
            // -- notes would appear to vanish depending on how the app started.
            Report("notesRoot", $"'{raw.NotesRoot}' is not an absolute path; used the default.");
            notesRoot = defaults.NotesRoot;
        }

        var defaultColor = raw.DefaultColor;
        if (!Enum.IsDefined(defaultColor))
        {
            Report("defaultColor", $"'{(int)raw.DefaultColor}' is not a note colour; used {defaults.DefaultColor}.");
            defaultColor = defaults.DefaultColor;
        }

        var theme = raw.Theme;
        if (!Enum.IsDefined(theme))
        {
            Report("theme", $"'{(int)raw.Theme}' is not a theme preference; used {defaults.Theme}.");
            theme = defaults.Theme;
        }

        var opacity = raw.DefaultOpacity;
        if (double.IsNaN(opacity) || double.IsInfinity(opacity)
            || opacity < MinOpacity || opacity > MaxOpacity)
        {
            var replacement = double.IsFinite(opacity)
                ? Math.Clamp(opacity, MinOpacity, MaxOpacity)
                : defaults.DefaultOpacity;
            Report("defaultOpacity", $"'{opacity}' is unusable; used {replacement}.");
            opacity = replacement;
        }

        var width = ClampEdge(
            raw.DefaultWidth, defaults.DefaultWidth, MinNoteWidth, "defaultWidth", Report);
        var height = ClampEdge(
            raw.DefaultHeight, defaults.DefaultHeight, MinNoteHeight, "defaultHeight", Report);

        var fontSize = raw.DefaultFontSizePx;
        if (fontSize < MinFontSizePx || fontSize > MaxFontSizePx)
        {
            // Reported even when absent, unlike a note's own size: this is one
            // line for the whole app rather than one per note, and a
            // settings.json that says 0 really is a value somebody wrote.
            var replacement = fontSize <= 0
                ? defaults.DefaultFontSizePx
                : Math.Clamp(fontSize, MinFontSizePx, MaxFontSizePx);
            Report("defaultFontSizePx", $"'{fontSize}' is unusable; used {replacement}.");
            fontSize = replacement;
        }

        var newNote = ValidateHotkey(
            raw.NewNoteHotkey, defaults.NewNoteHotkey, "newNoteHotkey", Report);
        var showHide = ValidateHotkey(
            raw.ShowHideHotkey, defaults.ShowHideHotkey, "showHideHotkey", Report);

        return raw with
        {
            NotesRoot = notesRoot,
            DefaultColor = defaultColor,
            Theme = theme,
            DefaultOpacity = opacity,
            DefaultWidth = width,
            DefaultHeight = height,
            DefaultFontSizePx = fontSize,
            NewNoteHotkey = newNote,
            ShowHideHotkey = showHide,
        };
    }

    /// <summary>
    /// A hotkey string that <see cref="HotkeySpec"/> cannot parse falls back
    /// to the default, and a parseable one is stored in its canonical form.
    /// </summary>
    /// <remarks>
    /// Rejecting only a blank is not enough:
    /// "Ctrl+Alt+Enter" and a bare "N" both deserialise fine, and both mean a
    /// hotkey the user configured that then silently never fires -- or, for the
    /// bare key, one that would swallow that key for every application on the
    /// machine. Canonicalising here as well as reporting keeps settings.json
    /// from holding two spellings of the same combination.
    /// </remarks>
    private static string ValidateHotkey(
        string raw, string fallback, string field, Action<string, string> report)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            report(field, $"was blank; used '{fallback}'.");
            return fallback;
        }

        if (!HotkeySpec.TryParse(raw, out var spec))
        {
            report(field, $"'{raw}' is not a usable hotkey; used '{fallback}'.");
            return fallback;
        }

        if (!string.Equals(spec.Canonical, raw, StringComparison.Ordinal))
            report(field, $"'{raw}' was written as '{spec.Canonical}'.");

        return spec.Canonical;
    }

    private static int ClampEdge(
        int raw, int fallback, int minimum, string field, Action<string, string> report)
    {
        if (raw <= 0)
        {
            report(field, $"{raw} is not a size; used {fallback}.");
            return fallback;
        }

        var clamped = Math.Clamp(raw, minimum, MaxNoteEdge);
        if (clamped != raw) report(field, $"{raw} is outside {minimum}-{MaxNoteEdge}; used {clamped}.");
        return clamped;
    }

    private static int ClampCoordinate(
        int raw, string field, Action<string, string> report)
    {
        var clamped = Math.Clamp(raw, -MaxCoordinate, MaxCoordinate);
        if (clamped != raw) report(field, $"{raw} is beyond +/-{MaxCoordinate}; used {clamped}.");
        return clamped;
    }

    /// <summary>
    /// A round-tripped DateTime can come back Unspecified or Local. Everything
    /// downstream -- Recent Notes ordering, the recovery envelope -- assumes
    /// UTC, so settle it here rather than at each comparison.
    /// </summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
