using System.Text;

namespace StickyMD.Core.Editing;

public static partial class MarkdownEditOps
{
    private const string IndentUnit = "  ";

    public static EditResult Indent(string text, int selStart, int selLen)
        => ShiftLines(text, selStart, selLen, outdent: false);

    public static EditResult Outdent(string text, int selStart, int selLen)
        => ShiftLines(text, selStart, selLen, outdent: true);

    private static EditResult ShiftLines(
        string text, int selStart, int selLen, bool outdent)
    {
        text ??= string.Empty;
        (selStart, selLen) = ClampSelection(text, selStart, selLen);

        var firstLineStart = LineStart(text, selStart);
        var lastLineEnd = LineEnd(text, selStart + selLen);

        var builder = new StringBuilder(text.Length + 16);
        builder.Append(text, 0, firstLineStart);

        var newStart = selStart;
        var newLength = selLen;
        var cursor = firstLineStart;
        var isSingleCaretOnNonList = false;

        while (cursor <= lastLineEnd)
        {
            var lineEnd = LineEnd(text, cursor);
            var line = text[cursor..lineEnd];
            var isList = ParseListPrefix(line) is not null;

            string newLine;
            int delta;

            if (outdent)
            {
                var removable = 0;
                while (removable < IndentUnit.Length
                       && removable < line.Length
                       && line[removable] == ' ')
                    removable++;

                newLine = line[removable..];
                delta = -removable;
            }
            else if (isList || selLen > 0 || cursor != firstLineStart)
            {
                newLine = IndentUnit + line;
                delta = IndentUnit.Length;
            }
            else
            {
                // Single caret on a non-list line: insert at the caret itself.
                isSingleCaretOnNonList = true;
                var offset = selStart - cursor;
                newLine = line[..offset] + IndentUnit + line[offset..];
                delta = IndentUnit.Length;
            }

            builder.Append(newLine);

            if (delta != 0)
            {
                if (isSingleCaretOnNonList)
                {
                    newStart += delta;
                }
                else if (cursor < selStart)
                {
                    // Line begins before the selection: shift start, and shift
                    // length too if the change lands before the selection start.
                    newStart += delta;
                }
                else
                {
                    newLength += delta;
                }
            }

            // Last line in range: stop WITHOUT appending a separator. The tail
            // append below starts at lastLineEnd, which is that newline, so
            // adding one here would duplicate it.
            if (lineEnd >= lastLineEnd) break;

            builder.Append('\n');
            cursor = lineEnd + 1;
        }

        if (lastLineEnd < text.Length)
            builder.Append(text, lastLineEnd, text.Length - lastLineEnd);

        var result = builder.ToString();

        newStart = Math.Clamp(newStart, 0, result.Length);
        newLength = Math.Clamp(newLength, 0, result.Length - newStart);

        return new EditResult(result, newStart, newLength);
    }
}
