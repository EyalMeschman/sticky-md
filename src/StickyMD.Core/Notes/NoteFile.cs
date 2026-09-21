using System.Security.Cryptography;
using System.Text;

namespace StickyMD.Core.Notes;

/// <summary>
/// Reads and writes a single note file without changing its physical format.
/// </summary>
public static class NoteFile
{
    public static NoteContent Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes);
        var raw = Decode(bytes, encoding);

        var newline = DetectNewline(raw);
        var trailing = raw.EndsWith('\n');
        var text = raw.Replace("\r\n", "\n");

        return new NoteContent(
            text,
            new NoteFormat(encoding, newline, trailing),
            Sha256(bytes));
    }

    public static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private static NoteEncoding DetectEncoding(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return NoteEncoding.Utf8Bom;
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return NoteEncoding.Utf16Le;
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return NoteEncoding.Utf16Be;
        return NoteEncoding.Utf8NoBom;
    }

    /// <summary>
    /// Decodes strictly. Without throwOnInvalidBytes a non-UTF8 file (a Windows
    /// ANSI note, say) would decode to U+FFFD and the next save would write those
    /// replacement characters back, destroying the original bytes silently. A
    /// loud DecoderFallbackException leaves the file untouched and lets the caller
    /// decide what to tell the user.
    /// </summary>
    private static string Decode(byte[] bytes, NoteEncoding encoding) => encoding switch
    {
        NoteEncoding.Utf8Bom => new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3),
        NoteEncoding.Utf16Le => new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2),
        NoteEncoding.Utf16Be => new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2),
        _ => new UTF8Encoding(false, true).GetString(bytes),
    };

    private static NoteNewline DetectNewline(string raw)
    {
        var crlf = 0;
        var loneLf = 0;

        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\n') continue;
            if (i > 0 && raw[i - 1] == '\r') crlf++;
            else loneLf++;
        }

        // Only \n is counted, so a bare-\r (classic Mac) file falls through to
        // the canonical default and its Text keeps the raw \r. NoteNewline has no
        // Cr member by design; such files are out of scope rather than handled.
        if (crlf == 0 && loneLf == 0) return NoteFormat.Canonical.Newline;
        return crlf >= loneLf ? NoteNewline.Crlf : NoteNewline.Lf;
    }

    /// <summary>What a completed write produced. Feeds the write ledger.</summary>
    public readonly record struct WriteOutcome(long Size, string ContentHash);

    private const string TempSuffix = ".stickymd-tmp";

    public static WriteOutcome AtomicWrite(string path, string text, NoteFormat format)
        => AtomicWriteBytes(path, Encode(text ?? string.Empty, format));

    /// <summary>
    /// Writes bytes so a reader never observes a partial file. The contract is
    /// atomicity, not a particular mechanism.
    /// </summary>
    public static WriteOutcome AtomicWriteBytes(string path, byte[] bytes)
    {
        var temp = path + TempSuffix;

        try
        {
            File.WriteAllBytes(temp, bytes);

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, destinationBackupFileName: null);
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
                {
                    // Some network and virtual filesystems reject ReplaceFile.
                    File.Move(temp, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return new WriteOutcome(bytes.LongLength, Sha256(bytes));
    }

    /// <summary>
    /// Encodes strictly, mirroring <see cref="Decode"/>. Lenient encoders substitute
    /// U+FFFD for a lone surrogate, which would write corruption to disk with no
    /// error -- and because the outcome hash is taken over these same bytes, the
    /// write ledger could never detect it. Encoding happens before any file is
    /// touched, so a throw leaves both the note on disk and the caller's buffer intact.
    /// </summary>
    private static byte[] Encode(string text, NoteFormat format)
    {
        var normalized = text.Replace("\r\n", "\n");

        if (format.TrailingNewline)
        {
            if (!normalized.EndsWith('\n')) normalized += "\n";
        }
        else if (normalized.EndsWith('\n'))
        {
            // Remove exactly ONE trailing newline -- the one whose absence this
            // format preserves. TrimEnd('\n') would delete every trailing blank
            // line the user typed.
            normalized = normalized[..^1];
        }

        if (format.Newline == NoteNewline.Crlf)
            normalized = normalized.Replace("\n", "\r\n");

        return format.Encoding switch
        {
            NoteEncoding.Utf8Bom =>
                [0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false, true).GetBytes(normalized)],
            NoteEncoding.Utf16Le =>
                [0xFF, 0xFE, .. new UnicodeEncoding(false, false, true).GetBytes(normalized)],
            NoteEncoding.Utf16Be =>
                [0xFE, 0xFF, .. new UnicodeEncoding(true, false, true).GetBytes(normalized)],
            _ => new UTF8Encoding(false, true).GetBytes(normalized),
        };
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
