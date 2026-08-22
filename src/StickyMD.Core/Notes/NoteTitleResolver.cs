namespace StickyMD.Core.Notes;

/// <summary>
/// Derives a note's display title from its content. The title is independent
/// of the filename; see the spec section "New note filenames".
/// </summary>
public static class NoteTitleResolver
{
    private const int MaxFallbackLength = 60;

    public static string Resolve(string content, string filePath)
    {
        var lines = (content ?? string.Empty).Split('\n');
        var inFence = false;
        string? firstNonEmpty = null;

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

            if (trimmed.StartsWith("# ", StringComparison.Ordinal) || trimmed == "#")
            {
                var heading = trimmed[1..].Trim().TrimEnd('#').Trim();
                if (heading.Length > 0) return heading;
            }

            if (i + 1 < lines.Length && IsSetextUnderline(lines[i + 1].TrimEnd('\r')))
                return trimmed;

            firstNonEmpty ??= trimmed;
        }

        if (firstNonEmpty is not null)
        {
            var stripped = firstNonEmpty.TrimStart('#').Trim();
            if (stripped.Length == 0) stripped = firstNonEmpty;
            return stripped.Length > MaxFallbackLength
                ? stripped[..MaxFallbackLength]
                : stripped;
        }

        return Path.GetFileNameWithoutExtension(filePath);
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
