using StickyMD.Core.Theming;

namespace StickyMD.Core.Persistence;

/// <summary>
/// Per-note StickyMD state. Geometry is in PHYSICAL screen pixels.
/// <paramref name="IsOpen"/> means "belongs on my desktop and returns next
/// startup" -- only an explicit Close Note may clear it.
/// </summary>
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
    DateTime LastOpenedUtc);

public sealed class NoteIndex
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public Dictionary<string, NoteState> Notes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
