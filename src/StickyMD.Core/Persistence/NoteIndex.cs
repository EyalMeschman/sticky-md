using StickyMD.Core.Geometry;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Persistence;

/// <summary>
/// Per-note StickyMD state. Geometry is in PHYSICAL screen pixels.
/// <paramref name="IsOpen"/> means "belongs on my desktop and returns next
/// startup" -- only an explicit Close Note may clear it.
/// </summary>
/// <param name="FontSizePx">
/// This note's text size in CSS pixels, per note like its colour and opacity.
///
/// ZERO MEANS "NOT SET", and it is the value every note in an index written
/// before this field existed deserialises to -- a missing JSON property leaves
/// the positional parameter at its default. StateValidator turns a zero into
/// <c>AppSettings.DefaultFontSizePx</c> SILENTLY for exactly that reason: an
/// index from an older build is being upgraded, not corrected, and reporting it
/// would put one correction line per note in diagnostics.log on the first run
/// after an update. An out-of-range value is a different thing and IS reported.
/// </param>
public sealed record NoteState(
    int X,
    int Y,
    int W,
    int H,
    string? Monitor,
    NoteColor Color,
    double Opacity,
    bool AlwaysOnTop,
    bool IsOpen,
    DateTime LastOpenedUtc,
    int FontSizePx = 0)
{
    // JsonIgnore, or System.Text.Json writes this derived rect into notes.json
    // as a ninth field beside the four it is computed from.
    [System.Text.Json.Serialization.JsonIgnore]
    public PixelRect Bounds => new(X, Y, W, H);

    /// <summary>This state with the window's live rectangle stamped onto it.</summary>
    public NoteState WithBounds(PixelRect rect)
        => this with { X = rect.X, Y = rect.Y, W = rect.Width, H = rect.Height };
}

public sealed class NoteIndex
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public Dictionary<string, NoteState> Notes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
