namespace StickyMD.Core.Markdown;

/// <param name="Applied">
/// False means the click was refused and <paramref name="Markdown"/> is the
/// unchanged input. Refusing is a correct outcome; corrupting is not.
/// </param>
public readonly record struct ToggleResult(bool Applied, string Markdown, string? Reason);

/// <summary>
/// Flips a task checkbox at an exact source span, validating the span before
/// touching anything. Span end is INCLUSIVE, matching Markdig.
/// </summary>
public static class TaskListToggler
{
    private const int TokenLength = 3;

    public static ToggleResult Toggle(string markdown, int spanStart, int spanEndInclusive)
    {
        if (markdown is null)
            return new ToggleResult(false, string.Empty, "Markdown was null.");

        if (spanStart < 0 || spanEndInclusive < spanStart)
            return new ToggleResult(false, markdown, "Span is inverted or negative.");

        var length = spanEndInclusive - spanStart + 1;

        if (length != TokenLength)
            return new ToggleResult(false, markdown,
                $"Span covers {length} characters; a task marker is {TokenLength}.");

        if (spanEndInclusive >= markdown.Length)
            return new ToggleResult(false, markdown, "Span extends past the document.");

        var token = markdown.Substring(spanStart, TokenLength);

        var replacement = token switch
        {
            "[ ]" => "[x]",
            "[x]" or "[X]" => "[ ]",
            _ => null,
        };

        if (replacement is null)
            return new ToggleResult(false, markdown,
                $"Span reads '{token}', which is not a task marker.");

        var updated = string.Concat(
            markdown.AsSpan(0, spanStart),
            replacement,
            markdown.AsSpan(spanEndInclusive + 1));

        return new ToggleResult(true, updated, null);
    }
}
