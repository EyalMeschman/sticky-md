using System.Text.RegularExpressions;

namespace StickyMD.Core.Editing;

public static partial class MarkdownEditOps
{
    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<marker>[-*+]|(?<number>\d+)\.)(?<gap>[ \t]+)(?<task>\[[ xX]\][ \t]+)?")]
    private static partial Regex ListPrefixRegex();

    internal sealed record ListPrefix(
        string Indent, string Marker, int? Number, string Gap, bool IsTask, int Length);

    public static EditResult ContinueList(string text, int selStart, int selLen)
    {
        text ??= string.Empty;
        (selStart, selLen) = ClampSelection(text, selStart, selLen);

        if (selLen > 0)
        {
            text = text.Remove(selStart, selLen);
            selLen = 0;
        }

        var lineStart = LineStart(text, selStart);
        var lineEnd = LineEnd(text, selStart);
        var line = text[lineStart..lineEnd];

        var prefix = ParseListPrefix(line);
        if (prefix is null) return new EditResult(text, selStart, 0);

        var rest = line[prefix.Length..];

        // Empty item -> strip the prefix and end the list. No newline added.
        if (rest.Trim().Length == 0)
        {
            var updated = text.Remove(lineStart, lineEnd - lineStart);
            return new EditResult(updated, lineStart, 0);
        }

        var nextMarker = prefix.Number is int n
            ? $"{n + 1}."
            : prefix.Marker;

        var insertion = "\n" + prefix.Indent + nextMarker + prefix.Gap
            + (prefix.IsTask ? "[ ] " : string.Empty);

        var withInsertion = text.Insert(selStart, insertion);
        return new EditResult(withInsertion, selStart + insertion.Length, 0);
    }

    internal static ListPrefix? ParseListPrefix(string line)
    {
        var match = ListPrefixRegex().Match(line);
        if (!match.Success) return null;

        var numberGroup = match.Groups["number"];
        return new ListPrefix(
            Indent: match.Groups["indent"].Value,
            Marker: match.Groups["marker"].Value,
            Number: numberGroup.Success ? int.Parse(numberGroup.Value) : null,
            Gap: match.Groups["gap"].Value,
            IsTask: match.Groups["task"].Success,
            Length: match.Length);
    }

    internal static int LineStart(string text, int index)
    {
        // A caret at 0 is always at a line start. Clamping index-1 up to 0 would
        // make LastIndexOf match the newline AT the caret, returning 1 while
        // LineEnd returns 0 -- and text[1..0] throws.
        if (index <= 0) return 0;

        var i = text.LastIndexOf('\n', Math.Min(index - 1, text.Length - 1));
        return i < 0 ? 0 : i + 1;
    }

    internal static int LineEnd(string text, int index)
    {
        var i = text.IndexOf('\n', Math.Min(index, text.Length));
        return i < 0 ? text.Length : i;
    }
}
