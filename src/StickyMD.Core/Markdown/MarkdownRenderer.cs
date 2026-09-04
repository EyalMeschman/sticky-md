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

        return $"https://{options.VirtualHost}/{relative}";
    }

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
