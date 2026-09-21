using System.Text;

namespace StickyMD.Core.Diagnostics;

/// <summary>
/// Append-only text log for things the user should be able to find out about
/// after the fact: validation corrections, exhausted save retries, WebView2
/// process failures.
/// </summary>
/// <remarks>
/// This is the "never die silently" backstop. The tray balloons only the two
/// cases a user has to act on; everything else -- a clamped note colour, a
/// dropped index entry -- would otherwise be a change with no explanation
/// anywhere, and this file is the record.
///
/// EVERY operation is best-effort and swallows its exceptions. This is the
/// channel error paths report THROUGH -- if it can throw, it converts a
/// handled problem into an unhandled one.
/// </remarks>
public static class DiagnosticsLog
{
    /// <summary>
    /// Rotation threshold. Small on purpose: this log is read by a human
    /// diagnosing one incident, not mined.
    /// </summary>
    public const int MaxBytes = 256 * 1024;

    public static void Write(string path, string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            RotateIfOversized(path);

            File.AppendAllText(
                path,
                $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch (IOException) { /* last-resort channel: never throw */ }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
    }

    /// <summary>
    /// Writes a heading followed by one line per item, or nothing at all when
    /// there are no items -- so a clean startup leaves no trace.
    /// </summary>
    public static void WriteAll(string path, string heading, IEnumerable<string> lines)
    {
        var items = lines as IReadOnlyCollection<string> ?? lines.ToList();
        if (items.Count == 0) return;

        Write(path, heading);
        foreach (var line in items) Write(path, "  " + line);
    }

    private static void RotateIfOversized(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxBytes) return;

            var previous = path + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(path, previous);
        }
        catch (IOException) { /* keep appending to an oversized file rather than losing the write */ }
        catch (UnauthorizedAccessException) { }
    }
}
