namespace StickyMD.Core.Editing;

/// <summary>The result of an edit: new text plus where the selection lands.</summary>
public readonly record struct EditResult(string Text, int SelectionStart, int SelectionLength);

/// <summary>
/// Editor conveniences as pure functions over (text, selection). No UI type
/// appears here, which is what makes every case unit-testable.
/// </summary>
public static partial class MarkdownEditOps
{
    public static EditResult ToggleBold(string text, int selStart, int selLen)
        => ToggleWrap(text, selStart, selLen, "**");

    public static EditResult ToggleItalic(string text, int selStart, int selLen)
        => ToggleWrap(text, selStart, selLen, "*");

    private static EditResult ToggleWrap(
        string text, int selStart, int selLen, string marker)
    {
        text ??= string.Empty;
        (selStart, selLen) = ClampSelection(text, selStart, selLen);

        var m = marker.Length;

        // Case 1: the selection itself includes the markers.
        if (selLen >= m * 2
            && Slice(text, selStart, m) == marker
            && Slice(text, selStart + selLen - m, m) == marker
            && !IsPartOfLongerRun(text, selStart, marker)
            && !IsPartOfLongerRun(text, selStart + selLen - m, marker))
        {
            var inner = text.Substring(selStart + m, selLen - (m * 2));
            var updated = text.Remove(selStart, selLen).Insert(selStart, inner);
            return new EditResult(updated, selStart, inner.Length);
        }

        // Case 2: the markers sit immediately outside the selection.
        if (selStart >= m
            && selStart + selLen + m <= text.Length
            && Slice(text, selStart - m, m) == marker
            && Slice(text, selStart + selLen, m) == marker
            && !IsPartOfLongerRun(text, selStart - m, marker)
            && !IsPartOfLongerRun(text, selStart + selLen, marker))
        {
            var updated = text
                .Remove(selStart + selLen, m)
                .Remove(selStart - m, m);
            return new EditResult(updated, selStart - m, selLen);
        }

        // Case 3: wrap.
        var wrapped = text
            .Insert(selStart + selLen, marker)
            .Insert(selStart, marker);
        return new EditResult(wrapped, selStart + m, selLen);
    }

    /// <summary>
    /// True when the marker at <paramref name="index"/> is part of a longer run
    /// of the same character. Stops a single-asterisk unwrap from tearing apart
    /// a "**" pair.
    /// </summary>
    private static bool IsPartOfLongerRun(string text, int index, string marker)
    {
        var c = marker[0];
        var runStart = index;
        while (runStart > 0 && text[runStart - 1] == c) runStart--;

        var runEnd = index + marker.Length;
        while (runEnd < text.Length && text[runEnd] == c) runEnd++;

        return runEnd - runStart > marker.Length;
    }

    private static string Slice(string text, int start, int length)
        => start < 0 || start + length > text.Length
            ? string.Empty
            : text.Substring(start, length);

    internal static (int Start, int Length) ClampSelection(
        string text, int selStart, int selLen)
    {
        var start = Math.Clamp(selStart, 0, text.Length);
        var length = Math.Clamp(selLen, 0, text.Length - start);
        return (start, length);
    }
}
