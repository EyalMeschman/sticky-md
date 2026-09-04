using System.Text.RegularExpressions;
using Shouldly;
using StickyMD.Core.Markdown;

namespace StickyMD.Core.Tests.Markdown;

public class MarkdownRendererTests
{
    private static readonly MarkdownRenderer Renderer = new();
    private static readonly RenderOptions Default = new(AllowRemoteImages: false);
    private static readonly RenderOptions RemoteAllowed = new(AllowRemoteImages: true);

    private static string Html(string markdown, RenderOptions? options = null)
        => Renderer.Render(markdown, options ?? Default).Html;

    // ---- the load-bearing assumption ----

    [Fact]
    public void Task_checkbox_span_indexes_exactly_the_bracket_token()
    {
        // If this fails, Markdig's precise source locations are not what the
        // checkbox feature assumes and Task 17 must be redesigned. Note that
        // SourceSpan.End is INCLUSIVE.
        const string markdown = "- [ ] buy milk";

        var (start, end) = FirstSpan(Html(markdown));

        markdown.Substring(start, end - start + 1).ShouldBe("[ ]");
    }

    [Fact]
    public void Span_is_correct_when_the_checkbox_is_not_at_the_start_of_the_document()
    {
        const string markdown = "# Shopping\n\nsome prose\n\n- [ ] buy milk\n";

        var (start, end) = FirstSpan(Html(markdown));

        markdown.Substring(start, end - start + 1).ShouldBe("[ ]");
    }

    [Fact]
    public void Every_checkbox_span_indexes_its_own_token()
    {
        const string markdown = "- [ ] one\n\nprose\n\n- [x] two\n- [ ] three\n";

        var spans = AllSpans(Html(markdown));

        spans.Count.ShouldBe(3);
        markdown.Substring(spans[0].Start, 3).ShouldBe("[ ]");
        markdown.Substring(spans[1].Start, 3).ShouldBe("[x]");
        markdown.Substring(spans[2].Start, 3).ShouldBe("[ ]");
    }

    [Fact]
    public void Checked_boxes_render_as_checked()
    {
        Html("- [x] done").ShouldContain("checked");
        Html("- [ ] not done").ShouldNotContain("checked");
    }

    [Fact]
    public void Checkboxes_render_as_inputs_with_span_attributes()
    {
        var html = Html("- [ ] a");

        html.ShouldContain("type=\"checkbox\"");
        html.ShouldContain("data-span-start=");
        html.ShouldContain("data-span-end=");
    }

    // ---- markdown fidelity ----

    [Fact]
    public void Renders_headings()
        => Html("# Title").ShouldContain("<h1");

    [Fact]
    public void Renders_tables_via_advanced_extensions()
        => Html("| a | b |\n|---|---|\n| 1 | 2 |").ShouldContain("<table");

    [Fact]
    public void Renders_fenced_code()
        => Html("```\ncode\n```").ShouldContain("<pre");

    [Fact]
    public void A_single_newline_becomes_a_hard_break()
    {
        // Nobody expects markdown soft-wrap semantics in a sticky note.
        Html("line one\nline two").ShouldContain("<br");
    }

    [Fact]
    public void Raw_html_is_escaped_not_executed()
    {
        var html = Html("<script>alert(1)</script>");

        html.ShouldNotContain("<script>");
        html.ShouldContain("&lt;script&gt;");
    }

    [Fact]
    public void Raw_html_attributes_are_escaped_too()
    {
        var html = Html("<img src=x onerror=alert(1)>");

        // The tag is inert TEXT, not markup. The attribute characters still appear --
        // and must, because the user typed them and stripping would lose their
        // content. What matters is that onerror is never a live attribute on a real
        // element, which escaping the angle brackets fully prevents.
        html.ShouldNotContain("<img");
        html.ShouldContain("&lt;img");
    }

    // ---- image policy ----

    [Fact]
    public void Relative_image_is_rewritten_to_the_virtual_host()
        => Html("![d](diagram.png)").ShouldContain("https://note.local/diagram.png");

    [Fact]
    public void Nested_relative_image_keeps_its_subpath()
        => Html("![d](images/diagram.png)")
            .ShouldContain("https://note.local/images/diagram.png");

    [Fact]
    public void Data_uri_image_is_left_alone()
    {
        const string uri = "data:image/png;base64,iVBORw0KGgo=";

        Html($"![d]({uri})").ShouldContain(uri);
    }

    [Fact]
    public void Remote_image_is_blocked_by_default()
    {
        var html = Html("![t](https://example.com/tracker?id=123)");

        html.ShouldContain(MarkdownRenderer.BlockedScheme);
        html.ShouldNotContain("src=\"https://example.com");
    }

    [Fact]
    public void Remote_image_is_allowed_when_the_option_is_set()
        => Html("![t](https://example.com/pic.png)", RemoteAllowed)
            .ShouldContain("https://example.com/pic.png");

    [Fact]
    public void Parent_relative_image_is_blocked()
        => Html("![d](../shared/diagram.png)")
            .ShouldContain(MarkdownRenderer.BlockedScheme);

    [Fact]
    public void Root_relative_image_is_blocked()
        => Html("![d](/rooted.png)").ShouldContain(MarkdownRenderer.BlockedScheme);

    [Fact]
    public void Absolute_windows_path_image_is_blocked()
        => Html(@"![d](C:\pics\x.png)").ShouldContain(MarkdownRenderer.BlockedScheme);

    [Fact]
    public void File_uri_image_is_blocked()
        => Html("![d](file:///C:/pics/x.png)").ShouldContain(MarkdownRenderer.BlockedScheme);

    [Fact]
    public void Links_are_not_treated_as_images()
    {
        // Only image URLs are rewritten. Link navigation is the host's job.
        var html = Html("[docs](https://example.com)");

        html.ShouldContain("https://example.com");
        html.ShouldNotContain(MarkdownRenderer.BlockedScheme);
    }

    [Fact]
    public void Percent_encoded_parent_traversal_is_blocked()
    {
        // A literal ".." check misses this, but the host decodes %2e%2e%2f to "../"
        // when it resolves the URL -- so the check must decode first.
        Html("![d](%2e%2e%2fsecrets.png)").ShouldContain(MarkdownRenderer.BlockedScheme);
    }

    [Fact]
    public void Two_dots_inside_a_filename_are_not_treated_as_traversal()
    {
        var html = Html("![d](notes..final.png)");

        html.ShouldNotContain(MarkdownRenderer.BlockedScheme);
        html.ShouldContain("https://note.local/notes..final.png");
    }

    [Fact]
    public void A_dot_prefixed_filename_keeps_its_leading_dot()
    {
        // TrimStart('.', '/') used to rewrite this to "hidden.png" -- a different file.
        Html("![d](.hidden.png)").ShouldContain("https://note.local/.hidden.png");
    }

    [Fact]
    public void A_leading_dot_slash_is_normalised_away()
    {
        Html("![d](./diagram.png)").ShouldContain("https://note.local/diagram.png");
    }

    [Fact]
    public void An_uppercase_remote_scheme_is_still_blocked()
        => Html("![t](HTTPS://example.com/pic.png)").ShouldContain(MarkdownRenderer.BlockedScheme);

    [Fact]
    public void A_protocol_relative_image_is_blocked()
        => Html("![t](//evil.com/pic.png)").ShouldContain(MarkdownRenderer.BlockedScheme);

    [Fact]
    public void A_query_string_is_encoded_into_the_filename_not_left_as_a_query()
    {
        // This USED to assert the query survived, as a cache-buster. Segment
        // escaping deliberately ends that: '#' is legal in a Windows filename
        // and truncated the URL at the fragment, so the whole segment is now
        // percent-encoded. '?' cannot appear in a Windows filename at all, so
        // nothing addressable is lost -- a link written this way was relying
        // on note.local's host quietly discarding the query, and there is no
        // server behind it for a query to mean anything to.
        Html("![d](img.png?v=2)").ShouldContain("https://note.local/img.png%3Fv%3D2");
    }

    [Fact]
    public void A_hash_in_a_filename_is_escaped_rather_than_truncating_the_url()
    {
        // Unescaped, "https://note.local/draft#2.png" asks for "draft" and
        // treats the rest as a fragment: the image silently never loads, with
        // no error anywhere. '#' is legal in a Windows filename.
        var html = Html("![d](draft#2.png)");

        html.ShouldContain("https://note.local/draft%232.png");
        html.ShouldNotContain(MarkdownRenderer.BlockedScheme);
    }

    [Fact]
    public void A_space_in_a_filename_is_escaped_and_subfolders_keep_their_separator()
    {
        // Pointy brackets are how CommonMark carries a space in a link
        // destination; without them Markdig does not treat this as an image
        // at all.
        Html("![d](<my images/note 1.png>)")
            .ShouldContain("https://note.local/my%20images/note%201.png");
    }

    [Fact]
    public void An_already_encoded_space_is_not_double_encoded()
    {
        // "%20" is the standard CommonMark encoding for a space in a link
        // destination. Escaping without decoding first yielded "%2520", the
        // host decoded once, and the request went out for a file literally
        // named "my%20file.png" -- a working link silently stopping working,
        // which is the failure class the escaping exists to close.
        Html("![d](my%20file.png)")
            .ShouldContain("https://note.local/my%20file.png");
    }

    // ---- token ----

    [Fact]
    public void Token_is_stable_for_identical_input()
        => Renderer.Render("# a", Default).Token
            .ShouldBe(Renderer.Render("# a", Default).Token);

    [Fact]
    public void Token_changes_when_the_markdown_changes()
        => Renderer.Render("# a", Default).Token
            .ShouldNotBe(Renderer.Render("# b", Default).Token);

    [Fact]
    public void Token_matches_ComputeToken()
        => Renderer.Render("# a", Default).Token
            .ShouldBe(MarkdownRenderer.ComputeToken("# a"));

    [Fact]
    public void Empty_markdown_renders_without_throwing()
        => Should.NotThrow(() => Renderer.Render("", Default));

    [Fact]
    public void Null_markdown_is_treated_as_empty()
        => Renderer.Render(null!, Default).Html.ShouldNotBeNull();

    // ---- blocked remote image count ----

    [Fact]
    public void A_clean_note_reports_no_blocked_remote_images()
    {
        var result = new MarkdownRenderer().Render(
            "# hi\n\n![local](pic.png)", new RenderOptions(AllowRemoteImages: false));

        result.BlockedRemoteImages.ShouldBe(0);
    }

    [Fact]
    public void Blocked_remote_images_are_counted()
    {
        var result = new MarkdownRenderer().Render(
            "![a](https://example.com/a.png)\n\n![b](http://example.com/b.png)",
            new RenderOptions(AllowRemoteImages: false));

        result.BlockedRemoteImages.ShouldBe(2);
    }

    [Fact]
    public void Allowed_remote_images_are_not_counted_as_blocked()
    {
        var result = new MarkdownRenderer().Render(
            "![a](https://example.com/a.png)",
            new RenderOptions(AllowRemoteImages: true));

        result.BlockedRemoteImages.ShouldBe(0);
    }

    [Fact]
    public void A_traversal_blocked_local_image_is_not_counted_as_a_remote_one()
    {
        // Enabling remote images would not make ../secret.png load, so
        // offering the "Load remote images" bar for it would be a lie.
        var result = new MarkdownRenderer().Render(
            "![x](../outside/secret.png)",
            new RenderOptions(AllowRemoteImages: false));

        result.Html.ShouldContain(MarkdownRenderer.BlockedScheme);
        result.BlockedRemoteImages.ShouldBe(0);
    }

    // ---- helpers ----

    private static readonly Regex SpanPair = new(
        @"data-span-start=""(?<start>\d+)""\s+data-span-end=""(?<end>\d+)""",
        RegexOptions.Compiled);

    private static (int Start, int End) FirstSpan(string html) => AllSpans(html)[0];

    private static List<(int Start, int End)> AllSpans(string html)
        => SpanPair.Matches(html)
            .Select(m => (int.Parse(m.Groups["start"].Value),
                          int.Parse(m.Groups["end"].Value)))
            .ToList();
}
