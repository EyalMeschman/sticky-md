namespace StickyMD.Core.Notes;

/// <summary>
/// Derives a note's display title: the first Markdown heading if the note has
/// one, otherwise the filename.
/// </summary>
/// <remarks>
/// An explicit heading wins because it is an explicit title -- a note opening
/// with "# Groceries" should say Groceries, not "2026-09-04-untitled".
///
/// THERE IS NO first-non-empty-line RULE, and there used to be. The spec and
/// Plan A both specified one (strip leading '#', truncate to 60), and it
/// survived every review because every test fed it prose. The first real note
/// anybody pasted in was a Markdown table, so the title became
/// "|Shortcut|Action|" -- and the user had already renamed the file to say
/// what the note was. The rule turned a fragment of content into a title and
/// silently overrode a name chosen on purpose. Any note starting with a table,
/// a list, a quote or a plain paragraph hit it.
///
/// The filename is the better fallback precisely because the user controls it,
/// through the rename prompt, and nothing else competes for it.
/// </remarks>
public static class NoteTitleResolver
{
    public static string Resolve(string content, string filePath)
    {
        var lines = (content ?? string.Empty).Split('\n');
        var inFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.Trim();

            if (IsFenceDelimiter(trimmed))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || trimmed.Length == 0) continue;

            // Any level, not just H1: a note whose only heading is "## Setup"
            // still means Setup. H1-only was why the old first-line rule had to
            // exist to cover sub-headings at all.
            if (TryAtxHeading(trimmed, out var heading)) return heading;

            if (i + 1 < lines.Length && IsSetextUnderline(lines[i + 1].TrimEnd('\r')))
                return trimmed;
        }

        return Path.GetFileNameWithoutExtension(filePath);
    }

    /// <summary>
    /// CommonMark ATX heading: one to six '#', then a space (or nothing).
    /// </summary>
    /// <remarks>
    /// The space is required by the spec and it matters here: without it
    /// "#hashtag" at the top of a note would be read as a title.
    /// </remarks>
    private static bool TryAtxHeading(string trimmed, out string heading)
    {
        heading = string.Empty;

        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;

        if (hashes is 0 or > 6) return false;
        if (hashes < trimmed.Length && trimmed[hashes] != ' ') return false;

        var text = trimmed[hashes..].Trim().TrimEnd('#').Trim();
        if (text.Length == 0) return false;

        heading = text;
        return true;
    }

    private static bool IsFenceDelimiter(string trimmed)
        => trimmed.StartsWith("```", StringComparison.Ordinal)
        || trimmed.StartsWith("~~~", StringComparison.Ordinal);

    private static bool IsSetextUnderline(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 && trimmed.All(c => c == '=');
    }
}
