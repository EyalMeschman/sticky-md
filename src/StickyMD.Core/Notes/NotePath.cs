namespace StickyMD.Core.Notes;

/// <summary>
/// The one canonical form of a note path, and the one comparer for it.
/// </summary>
/// <remarks>
/// Plan A shipped four disagreeing answers to "what is a note's path?":
/// NoteRepository handed out verbatim-joined paths, NoteWatcher emitted
/// GetFullPath-normalised ones, RecoveryStore hashed a lowercased variant, and
/// NoteIndexStore accepted whatever key was in the JSON. A path from one would
/// not compare equal to a path from another, so any map keyed by note path --
/// the window manager's open-notes map above all -- would silently miss and
/// open a second window on a file that was already open.
///
/// THE RULES:
///  1. Canonical form is GetFullPath, with any trailing separator trimmed.
///  2. Case is PRESERVED. The on-disk case is what the user sees in Explorer
///     and in notes.json; lowercasing it would write a wrong-looking path into
///     a user-facing file and destroy the only display-quality name we have.
///  3. Comparison is ALWAYS <see cref="Comparer"/>, which is case-insensitive.
///     That is what makes rule 2 free.
///  4. Canonicalise where a path is PRODUCED, never where it is consumed. Any
///     API handing out a note path returns canonical form, so no caller has to
///     remember to normalise an input it was given.
///
/// KNOWN LIMIT: GetFullPath does not resolve symlinks, junctions, 8.3 short
/// names, or the true on-disk casing. Those need an open handle and Win32
/// (GetFinalPathNameByHandle), which Core may not touch and which fails for a
/// file that does not exist yet -- and canonicalising the path of a note about
/// to be CREATED is a requirement here. So the same file reached through a
/// symlink and through its target still compares unequal. Accepted for v1, and
/// recorded rather than hidden.
/// </remarks>
public static class NotePath
{
    /// <summary>
    /// The only comparer any note-path-keyed dictionary, set, or equality test
    /// may use. Windows filesystems are case-insensitive in every configuration
    /// StickyMD supports.
    /// </summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A note path cannot be empty.", nameof(path));

        // TrimEndingDirectorySeparator, not TrimEnd('\\'): it deliberately
        // leaves a ROOT alone, so "C:\" survives intact. TrimEnd would produce
        // "C:", which is drive-relative and names a different location.
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>
    /// Canonicalises without throwing. Used on paths arriving from outside the
    /// app -- FileSystemWatcher event arguments, a hand-edited notes.json --
    /// where an unusable value must cost one entry, not the whole operation.
    /// </summary>
    public static bool TryCanonical(string? path, out string canonical)
    {
        canonical = string.Empty;

        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            canonical = Canonical(path);
            return true;
        }
        catch (ArgumentException) { return false; }      // embedded NUL, bad chars
        catch (NotSupportedException) { return false; }  // e.g. a stray colon
        catch (IOException) { return false; }            // covers PathTooLongException
        catch (System.Security.SecurityException) { return false; }
    }

    /// <summary>True when both sides name the same note.</summary>
    public static bool AreSame(string? a, string? b)
        => TryCanonical(a, out var ca)
        && TryCanonical(b, out var cb)
        && Comparer.Equals(ca, cb);

    /// <summary>
    /// True when <paramref name="path"/> is already canonical. Used by tests to
    /// assert the "canonicalise at production" rule at every boundary.
    /// </summary>
    public static bool IsCanonical(string path)
        => TryCanonical(path, out var canonical)
        && string.Equals(path, canonical, StringComparison.Ordinal);

    /// <summary>A dictionary keyed by note path, with the right comparer.</summary>
    public static Dictionary<string, TValue> NewMap<TValue>() => new(Comparer);
}
