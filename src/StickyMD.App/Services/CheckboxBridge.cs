using StickyMD.Core.Markdown;

namespace StickyMD.App.Services;

public enum ToggleVerdict
{
    /// <summary>Write <c>Markdown</c> to the buffer and save.</summary>
    Apply,

    /// <summary>
    /// The buffer moved since the render this click came from. Drop the click
    /// and re-render.
    /// </summary>
    StaleToken,

    /// <summary>The span does not name a task marker. Drop it and re-render.</summary>
    InvalidSpan,
}

/// <param name="Markdown">
/// The updated text on <see cref="ToggleVerdict.Apply"/>, and the UNCHANGED
/// input otherwise. A refusal must never hand back something half-edited.
/// </param>
public sealed record ToggleDecision(
    ToggleVerdict Verdict, string Markdown, string? Reason);

/// <summary>
/// Decides what a checkbox click does. Extracted from the window because this
/// is the one place a mistake writes the wrong characters into the user's file.
/// </summary>
/// <remarks>
/// TWO GUARDS, IN THIS ORDER.
///
/// The TOKEN closes the render-to-click race. Source spans survive document
/// edits that ordinals would not, but between rendering and clicking the buffer
/// can still move -- an external edit landing, or an autosave of a concurrent
/// edit. The token is the SHA-256 of the markdown at render time; a mismatch
/// means the span belongs to a document that no longer exists.
///
/// The SPAN CHECK is TaskListToggler's, and it is not duplicated here. Markdig's
/// SourceSpan.End is INCLUSIVE, so a marker span is exactly three characters
/// and anything else is refused. Validating spans in two places is how the two
/// places end up disagreeing.
///
/// The guaranteed failure mode is "rarely ignores a click and refreshes", never
/// "edits the wrong task". Both guards fail closed to preserve that.
/// </remarks>
public static class CheckboxBridge
{
    public static ToggleDecision Decide(
        string buffer, int spanStart, int spanEnd, string? token)
    {
        if (string.IsNullOrEmpty(token)
            || !string.Equals(
                token,
                MarkdownRenderer.ComputeToken(buffer),
                StringComparison.OrdinalIgnoreCase))
        {
            return new ToggleDecision(
                ToggleVerdict.StaleToken,
                buffer,
                "the note changed since it was rendered");
        }

        var result = TaskListToggler.Toggle(buffer, spanStart, spanEnd);

        return result.Applied
            ? new ToggleDecision(ToggleVerdict.Apply, result.Markdown, null)
            : new ToggleDecision(ToggleVerdict.InvalidSpan, buffer, result.Reason);
    }
}
