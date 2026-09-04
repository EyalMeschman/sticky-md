using StickyMD.Core.Notes;

namespace StickyMD.Core.Markdown;

public enum NavigationAction
{
    /// <summary>Refuse it. The default for anything not explicitly listed.</summary>
    Block,

    /// <summary>Hand it to the OS default browser. <c>Target</c> is the URL.</summary>
    OpenInBrowser,

    /// <summary>Open it as a StickyMD note. <c>Target</c> is a canonical path.</summary>
    OpenNote,

    /// <summary>The shell's own initial load. Must not be cancelled.</summary>
    AllowShellLoad,
}

/// <param name="Target">
/// A URL for <see cref="NavigationAction.OpenInBrowser"/>, a canonical note
/// path for <see cref="NavigationAction.OpenNote"/>, null otherwise.
/// </param>
/// <param name="Reason">
/// Why, in a form worth writing to the diagnostics log. A blocked link that
/// leaves no trace is indistinguishable from a broken one.
/// </param>
public sealed record NavigationDecision(
    NavigationAction Action, string? Target, string Reason);

/// <summary>
/// The spec's resource-and-navigation allowlist, as a pure decision. Nothing
/// here launches anything; the App layer performs the action.
/// </summary>
/// <remarks>
/// TWO ENTRY POINTS, ON PURPOSE.
///
/// <see cref="DecideLinkClick"/> is the real allowlist. Links arrive as a
/// 'link' web message carrying the RAW href attribute, because the shell is
/// loaded by NavigateToString and therefore has an opaque origin: the
/// browser's own resolution of "other.md" against about:blank is useless, and
/// fragment navigation there does not reliably raise NavigationStarting at
/// all.
///
/// <see cref="DecideNavigation"/> is the defensive backstop on
/// NavigationStarting and NewWindowRequested. It allows exactly one thing --
/// the shell's own initial load -- and blocks everything else. The spec's
/// "cancel unconditionally" is right in spirit and wrong in detail: cancelling
/// the NavigateToString load leaves the note permanently blank, with no
/// exception and nothing in any log to say why.
///
/// Nothing is allowed by default in either function, so a Markdown link
/// carrying an unexpected or hostile scheme is rejected rather than executed
/// inside the WebView.
///
/// KNOWN v1 LIMIT (spec): a path escaping the note directory
/// (../shared/x.png) does not resolve, and is blocked here.
/// </remarks>
public static class NavigationPolicy
{
    public static NavigationDecision DecideNavigation(string? uri, bool shellLoaded)
    {
        // Two allowances, and both are exact. This function's whole job is to
        // be the narrow backstop, so it is safe by construction rather than
        // by the circumstance that WebView2 has never yet sent it something
        // else at this point in the lifecycle.
        //
        // "about:blank" is kept for defence in depth, but it is NOT what a
        // real WebView2 runtime reports for NavigateToString: that API is
        // itself implemented as a navigation to a "data:text/html" URI built
        // from the supplied HTML, and NavigationStarting's Uri is that data
        // URI, not "about:blank". A live run against the actual runtime is
        // what surfaced this -- unit tests calling this function directly
        // could not, because they choose the uri they pass in. Cancelling
        // either form is how a note ends up permanently blank with no error
        // anywhere.
        if (!shellLoaded
            && uri is not null
            && (uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)))
        {
            return new NavigationDecision(
                NavigationAction.AllowShellLoad, null, "The note shell's initial load.");
        }

        return new NavigationDecision(
            NavigationAction.Block,
            null,
            $"Navigation to '{uri ?? "(null)"}' was refused; the note shell never navigates.");
    }

    public static NavigationDecision DecideLinkClick(string? href, string noteDirectory)
    {
        if (string.IsNullOrWhiteSpace(href))
            return Blocked(href, "the link had no target");

        var trimmed = href.Trim();

        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            // note.local is for subresource loads only. Navigating to it would
            // leave the shell -- and its CSP -- behind. Trim a single trailing
            // "." first: DNS treats "note.local." as the same name, but Uri
            // does not normalise it away, so an FQDN-style trailing dot would
            // otherwise slip past a plain equality check.
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var url)
                && TrimTrailingDot(url.Host).Equals(
                    "note.local", StringComparison.OrdinalIgnoreCase))
            {
                return Blocked(trimmed, "the virtual host is not a navigation target");
            }

            return new NavigationDecision(
                NavigationAction.OpenInBrowser, trimmed, "Opened in the default browser.");
        }

        // Any other scheme -- javascript:, data:, file:, ms-msdt:, mailto:,
        // and everything nobody has thought of -- is refused. An allowlist
        // that grows by sympathy is not an allowlist.
        if (HasScheme(trimmed))
            return Blocked(trimmed, "its scheme is not allowed");

        // The bridge script scrolls in-note anchors itself and never posts
        // them, so one arriving here means something unexpected happened.
        if (trimmed.StartsWith('#'))
            return Blocked(trimmed, "in-note anchors are handled in the page");

        // Decode ONCE before the traversal test -- that is the depth the
        // filesystem resolves at, and without it "%2e%2e%2f" sails past a
        // literal ".." check. Mirrors MarkdownRenderer.ResolveImageUrl.
        string decoded;
        try { decoded = Uri.UnescapeDataString(trimmed); }
        catch (UriFormatException) { return Blocked(trimmed, "it is not a decodable path"); }

        // Drop a fragment: v1 cannot scroll to a heading in another note, and
        // opening the file is still the useful half.
        var hash = decoded.IndexOf('#');
        if (hash >= 0) decoded = decoded[..hash];

        if (decoded.Length == 0)
            return Blocked(trimmed, "the link resolved to nothing");

        if (!decoded.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return Blocked(trimmed, "only .md links open as notes");

        if (EscapesDirectory(decoded))
            return Blocked(trimmed, "it points outside the note's folder");

        if (!NotePath.TryCanonical(Path.Combine(noteDirectory, decoded), out var target))
            return Blocked(trimmed, "it does not resolve to a usable path");

        // Belt and braces: even after the segment test, confirm the resolved
        // path really is inside the note directory. The string test can be
        // fooled by something the segment split did not anticipate; this
        // cannot.
        if (!NotePath.TryCanonical(noteDirectory, out var root)
            || !target.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(trimmed, "it resolved outside the note's folder");
        }

        return new NavigationDecision(
            NavigationAction.OpenNote, target, "Opened as a StickyMD note.");
    }

    private static NavigationDecision Blocked(string? href, string why)
        => new(NavigationAction.Block, null, $"Blocked '{href ?? "(null)"}': {why}.");

    private static string TrimTrailingDot(string host)
        => host.EndsWith('.') ? host[..^1] : host;

    /// <summary>
    /// A single-letter drive prefix such as "C:\elsewhere\x.md" is blocked
    /// HERE, not by <see cref="EscapesDirectory"/> -- "C:" matches the generic
    /// scheme pattern (a letter run followed by a colon), so it is refused as
    /// an unrecognised scheme before the path check ever runs. The outcome is
    /// the same either way; if you go looking for why a drive-letter path is
    /// blocked, look here first.
    /// </summary>
    private static bool HasScheme(string url)
        => url.Contains("://", StringComparison.Ordinal)
        || System.Text.RegularExpressions.Regex.IsMatch(
            url, @"^[A-Za-z][A-Za-z0-9+.\-]*:");

    /// <summary>
    /// Mirrors <c>MarkdownRenderer.EscapesNoteDirectory</c>. A ".." SEGMENT
    /// escapes; two dots merely inside a filename ("notes..final.md") do not,
    /// and blocking those was wrong.
    /// </summary>
    private static bool EscapesDirectory(string url)
    {
        if (url.StartsWith('/') || url.StartsWith('\\')) return true;
        if (url.Length > 1 && url[1] == ':') return true;

        foreach (var segment in url.Split('/', '\\'))
        {
            if (segment == "..") return true;

            // A colon outside the drive-letter position is exclusively NTFS
            // Alternate-Data-Stream syntax -- never a legitimate filename
            // character -- so "sub/name:stream.md" must be refused here even
            // though it never leaves the note directory and HasScheme's
            // anchored regex does not see a colon that is not at position 0.
            if (segment.Contains(':')) return true;
        }

        return false;
    }
}
