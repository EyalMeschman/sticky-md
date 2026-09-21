using System.Text.RegularExpressions;
using Shouldly;
using StickyMD.Core.Markdown;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Tests.Markdown;

public class HtmlDocumentBuilderTests
{
    private static readonly NoteTheme Theme =
        NotePalette.Get(NoteColor.Yellow, ThemeMode.Light);

    private static HtmlShellOptions Options(bool allowRemote = false)
        => new(Theme, allowRemote);

    [Fact]
    public void The_shell_stylesheet_carries_the_notes_own_font_size()
    {
        // Per note, so it has to reach the CSS rather than being a constant in
        // it. Every heading and code size is an em multiple of this one rule,
        // which is what makes one number scale the whole note.
        var shell = HtmlDocumentBuilder.BuildShell(Options() with { FontSizePx = 27 });

        shell.ShouldContain("font-size: 27px;");
    }

    [Fact]
    public void The_shell_font_size_defaults_to_the_one_canonical_number()
        => HtmlDocumentBuilder.BuildShell(Options())
            .ShouldContain($"font-size: {HtmlDocumentBuilder.DefaultFontSizePx}px;");

    [Fact]
    public void The_default_csp_allows_only_the_virtual_host_and_data_images()
    {
        var csp = HtmlDocumentBuilder.BuildCsp(allowRemoteImages: false, "N");

        csp.ShouldContain("img-src https://note.local data:");
        csp.ShouldNotContain("https:;");
        csp.ShouldContain("default-src 'none'");
    }

    [Fact]
    public void Enabling_remote_images_adds_https_to_img_src_only()
    {
        var csp = HtmlDocumentBuilder.BuildCsp(allowRemoteImages: true, "N");

        csp.ShouldContain("img-src https://note.local data: https:");
        csp.ShouldContain("default-src 'none'");
        csp.ShouldContain("connect-src 'none'");
    }

    [Fact]
    public void The_csp_forbids_everything_the_note_has_no_use_for()
    {
        var csp = HtmlDocumentBuilder.BuildCsp(allowRemoteImages: true, "N");

        // A note renders text and images. It never fetches, submits, frames, or
        // resolves a relative URL -- and a synced note is exactly the place a
        // hostile payload would try to.
        csp.ShouldContain("connect-src 'none'");
        csp.ShouldContain("form-action 'none'");
        csp.ShouldContain("frame-ancestors 'none'");
        csp.ShouldContain("base-uri 'none'");
        csp.ShouldContain("object-src 'none'");
    }

    [Fact]
    public void The_shell_embeds_the_csp_as_a_meta_tag()
    {
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        shell.ShouldContain("http-equiv=\"Content-Security-Policy\"");

        var nonce = Regex.Match(shell, "nonce-([A-Za-z0-9+/=]+)").Groups[1].Value;
        nonce.ShouldNotBeNullOrEmpty();

        shell.ShouldContain(HtmlDocumentBuilder.BuildCsp(false, nonce));
    }

    [Fact]
    public void The_shell_carries_every_note_css_variable()
    {
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        foreach (var (name, value) in NotePalette.ToCssVariables(Theme))
            shell.ShouldContain($"{name}: {value}");
    }

    [Fact]
    public void The_shell_uses_the_palette_rather_than_a_hardcoded_colour()
    {
        var yellow = HtmlDocumentBuilder.BuildShell(Options());
        var charcoal = HtmlDocumentBuilder.BuildShell(
            new HtmlShellOptions(
                NotePalette.Get(NoteColor.Charcoal, ThemeMode.Dark), false));

        yellow.ShouldNotBe(charcoal);
        charcoal.ShouldContain(
            NotePalette.Get(NoteColor.Charcoal, ThemeMode.Dark).ContentBg);
    }

    [Fact]
    public void Script_and_style_run_under_a_nonce_rather_than_unsafe_inline()
    {
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        shell.ShouldNotContain("'unsafe-inline'");

        var nonce = Regex.Match(shell, "nonce-([A-Za-z0-9+/=]+)").Groups[1].Value;
        nonce.ShouldNotBeNullOrEmpty();

        shell.ShouldContain($"<style nonce=\"{nonce}\">");
        shell.ShouldContain($"<script nonce=\"{nonce}\">");
    }

    [Fact]
    public void Every_shell_gets_a_fresh_nonce()
    {
        var a = Regex.Match(HtmlDocumentBuilder.BuildShell(Options()), "nonce-([^']+)").Groups[1].Value;
        var b = Regex.Match(HtmlDocumentBuilder.BuildShell(Options()), "nonce-([^']+)").Groups[1].Value;

        a.ShouldNotBe(b);
    }

    [Fact]
    public void The_shell_declares_the_content_container_the_bridge_writes_into()
    {
        HtmlDocumentBuilder.BuildShell(Options())
            .ShouldContain("id=\"content\"");
    }

    [Fact]
    public void The_bridge_script_handles_the_render_message()
    {
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        shell.ShouldContain("'render'");
        shell.ShouldContain("scrollTop");
        shell.ShouldContain("innerHTML");
    }

    [Fact]
    public void The_bridge_script_posts_the_four_outbound_message_types()
    {
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        shell.ShouldContain("'ready'");
        shell.ShouldContain("'toggleTask'");
        shell.ShouldContain("'link'");
        shell.ShouldContain("'requestEdit'");
    }

    [Fact]
    public void The_bridge_script_sends_the_render_token_back_with_a_toggle()
    {
        // The token is what makes a stale click safe to refuse rather than
        // apply to the wrong task.
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        shell.ShouldContain("spanStart");
        shell.ShouldContain("spanEnd");
        shell.ShouldContain("token");
    }

    [Fact]
    public void The_bridge_script_cancels_the_checkbox_default_action()
    {
        // The checkbox must not appear to toggle before the host has decided.
        // A refused click that already flipped visually is a checkbox that
        // lies about the file.
        HtmlDocumentBuilder.BuildShell(Options())
            .ShouldContain("preventDefault");
    }

    [Fact]
    public void The_bridge_script_sends_the_raw_href_not_the_resolved_one()
    {
        // getAttribute('href'), never a.href: the shell has an opaque origin,
        // so a.href resolves "notes.md" against about:blank and the host would
        // receive something it cannot map back to a file.
        HtmlDocumentBuilder.BuildShell(Options())
            .ShouldContain("getAttribute('href')");
    }

    [Fact]
    public void The_bridge_script_handles_anchors_itself()
    {
        // Fragment navigation inside an opaque-origin document does not
        // reliably reach NavigationStarting, so in-note anchors are scrolled
        // in the page rather than round-tripped through the host.
        HtmlDocumentBuilder.BuildShell(Options())
            .ShouldContain("scrollIntoView");
    }

    [Fact]
    public void Blocked_images_are_replaced_with_a_placeholder_by_the_script()
    {
        var shell = HtmlDocumentBuilder.BuildShell(Options());

        shell.ShouldContain(MarkdownRenderer.BlockedScheme);
        shell.ShouldContain("blocked-image");
    }

    [Fact]
    public void The_shell_is_small_enough_to_navigate_to_as_a_string()
    {
        // NavigateToString has a 2MB limit. This is a guard against someone
        // pasting a stylesheet in here later.
        HtmlDocumentBuilder.BuildShell(Options()).Length.ShouldBeLessThan(64 * 1024);
    }
}
