using System.Text;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using StickyMD.Core.Notes;

namespace StickyMD.Core.Markdown;

/// <param name="AllowRemoteImages">
/// False by default. Notes sync, and a shared note must not make network
/// requests just because StickyMD rendered it.
/// </param>
public sealed record RenderOptions(bool AllowRemoteImages, string VirtualHost = "note.local");

/// <param name="Token">
/// SHA-256 of the markdown at render time. A checkbox click carries it back so
/// a stale click is rejected rather than misapplied.
/// </param>
/// <param name="BlockedRemoteImages">
/// How many remote images the resource policy refused. Drives the per-note
/// "Load remote images" bar, which must not appear for a note that has none.
/// </param>
public sealed record RenderResult(string Html, string Token, int BlockedRemoteImages = 0);

public sealed class MarkdownRenderer
{
    /// <summary>Marks an image the resource policy refused to load.</summary>
    public const string BlockedScheme = "stickymd-blocked:";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .UsePreciseSourceLocation()
        .DisableHtml()
        .Build();

    public static string ComputeToken(string markdown)
        => NoteFile.Sha256(new UTF8Encoding(false).GetBytes(markdown ?? string.Empty));

    public RenderResult Render(string markdown, RenderOptions options)
    {
        markdown ??= string.Empty;

        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var blockedRemote = RewriteImageUrls(document, options);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<HtmlTaskListRenderer>(new SourceSpanTaskListRenderer());
        renderer.Render(document);
        writer.Flush();

        return new RenderResult(writer.ToString(), ComputeToken(markdown), blockedRemote);
    }

    /// <returns>How many REMOTE images were blocked. A traversal-blocked local
    /// image is not counted: enabling remote images would not make it load, so
    /// offering that bar for one would be a lie.</returns>
    private static int RewriteImageUrls(MarkdownDocument document, RenderOptions options)
    {
        var blockedRemote = 0;

        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!link.IsImage) continue;

            var original = link.Url;
            link.Url = ResolveImageUrl(original, options);

            if (link.Url.StartsWith(BlockedScheme, StringComparison.Ordinal)
                && IsRemote(original))
            {
                blockedRemote++;
            }
        }

        return blockedRemote;
    }

    private static bool IsRemote(string? url)
    {
        var trimmed = url?.Trim() ?? string.Empty;
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveImageUrl(string? url, RenderOptions options)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        var trimmed = url.Trim();

        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return options.AllowRemoteImages ? trimmed : BlockedScheme + trimmed;
        }

        // Decode once before the traversal test -- that is the depth the host will
        // resolve at, and without it "%2e%2e%2f" sails past a literal ".." check.
        if (EscapesNoteDirectory(Uri.UnescapeDataString(trimmed)))
            return BlockedScheme + trimmed;

        // Strip only a leading "./" -- TrimStart('.', '/') would turn a legitimate
        // ".hidden.png" into "hidden.png", silently requesting a different file.
        var relative = trimmed.StartsWith("./", StringComparison.Ordinal)
            ? trimmed[2..]
            : trimmed;

        return $"https://{options.VirtualHost}/{EscapePath(relative)}";
    }

    /// <summary>
    /// Percent-encodes each path segment, joining with '/'.
    /// </summary>
    /// <remarks>
    /// Unencoded, a filename containing '#' TRUNCATES the URL -- everything
    /// after it becomes a fragment and the note requests a file that does not
    /// exist, with no error anywhere. '#' is legal in a Windows filename, so
    /// this is reachable with an ordinary note.
    ///
    /// Separators are NOT escaped -- both '/' and '\' are normalised to '/' --
    /// so "images/diagram.png" and "images\diagram.png" both still address the
    /// subfolder.
    ///
    /// UNESCAPE BEFORE ESCAPE, and it is not a redundant round trip. "%20" is
    /// the standard CommonMark encoding for a space in a link destination, so
    /// encoding blindly turns "my%20file.png" into "my%2520file.png"; the host
    /// decodes once and asks for a file literally named "my%20file.png". A
    /// working link stops working, silently -- the exact failure class this
    /// method exists to close. Decoding first means the input is normalised to
    /// the depth the host resolves at, matching the traversal test above and
    /// NavigationPolicy, which decodes ".md" hrefs before resolving them.
    ///
    /// A query string does not survive that: "img.png?v=2" becomes a file
    /// named "img.png?v=2". Deliberate -- there is no server behind note.local
    /// for a query to mean anything to, and '?' cannot appear in a Windows
    /// filename, so such a link was relying on the host quietly discarding it.
    /// </remarks>
    private static string EscapePath(string relative)
        => string.Join(
            '/',
            relative.Split('/', '\\')
                .Select(s => Uri.EscapeDataString(Uri.UnescapeDataString(s))));

    private static bool EscapesNoteDirectory(string url)
    {
        if (url.StartsWith('/') || url.StartsWith('\\')) return true;
        if (url.Contains("://", StringComparison.Ordinal)) return true;
        if (url.Length > 1 && url[1] == ':') return true;

        // A ".." SEGMENT escapes the note directory. Two dots merely inside a
        // filename ("notes..final.png") do not, and blocking those was wrong.
        foreach (var segment in url.Split('/', '\\'))
        {
            if (segment == "..") return true;
        }

        return false;
    }
}
