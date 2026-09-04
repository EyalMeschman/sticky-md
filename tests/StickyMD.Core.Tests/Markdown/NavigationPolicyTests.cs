using Shouldly;
using StickyMD.Core.Markdown;

namespace StickyMD.Core.Tests.Markdown;

public class NavigationPolicyTests
{
    private const string NoteDir = @"C:\Notes";

    private static NavigationDecision Click(string? href)
        => NavigationPolicy.DecideLinkClick(href, NoteDir);

    [Fact]
    public void An_https_link_opens_in_the_default_browser()
    {
        var decision = Click("https://example.com/page");

        decision.Action.ShouldBe(NavigationAction.OpenInBrowser);
        decision.Target.ShouldBe("https://example.com/page");
    }

    [Fact]
    public void An_http_link_opens_in_the_default_browser()
        => Click("http://example.com").Action.ShouldBe(NavigationAction.OpenInBrowser);

    [Fact]
    public void A_relative_markdown_link_opens_as_a_note()
    {
        var decision = Click("other.md");

        decision.Action.ShouldBe(NavigationAction.OpenNote);
        decision.Target.ShouldBe(@"C:\Notes\other.md");
    }

    [Fact]
    public void A_relative_markdown_link_with_a_leading_dot_slash_opens_as_a_note()
        => Click("./other.md").Target.ShouldBe(@"C:\Notes\other.md");

    [Fact]
    public void A_percent_encoded_markdown_link_is_decoded_before_resolving()
    {
        Click("my%20note.md").Target.ShouldBe(@"C:\Notes\my note.md");
    }

    [Fact]
    public void A_markdown_link_with_a_fragment_opens_the_file_and_drops_the_fragment()
    {
        // v1 has no scroll-to-heading-in-another-note. Opening the file is the
        // useful part; silently doing nothing would not be.
        Click("other.md#section").Target.ShouldBe(@"C:\Notes\other.md");
    }

    [Fact]
    public void A_relative_link_that_is_not_markdown_is_blocked()
    {
        // Handing an arbitrary relative path to the shell would make any file
        // in the note directory launchable from a synced note.
        Click("script.ps1").Action.ShouldBe(NavigationAction.Block);
        Click("thing.pdf").Action.ShouldBe(NavigationAction.Block);
    }

    [Theory]
    [InlineData("../outside/x.md")]
    [InlineData("..\\outside\\x.md")]
    [InlineData("sub/../../x.md")]
    [InlineData("%2e%2e%2fx.md")]
    [InlineData("/absolute.md")]
    [InlineData("\\absolute.md")]
    [InlineData("C:\\elsewhere\\x.md")]
    public void A_link_escaping_the_note_directory_is_blocked(string href)
    {
        // %2e%2e%2f is the reason decoding happens BEFORE the traversal test --
        // a literal ".." check sails straight past it.
        Click(href).Action.ShouldBe(NavigationAction.Block);
    }

    [Theory]
    [InlineData("sub/name:stream.md")]
    [InlineData("./name:stream.md")]
    [InlineData("a/b:c.md")]
    public void A_colon_inside_a_path_segment_is_blocked_as_ads_syntax(string href)
    {
        // A colon outside the drive-letter position is exclusively NTFS
        // Alternate-Data-Stream syntax, never a legitimate filename character.
        // It never leaves the note directory and the scheme regex does not
        // see it (the colon is not at position 0), so EscapesDirectory must
        // catch it explicitly.
        Click(href).Action.ShouldBe(NavigationAction.Block);
    }

    [Fact]
    public void A_leading_colon_stays_blocked_because_it_is_not_a_dot_md_link()
    {
        // Same shape one character earlier: blocked here because it never
        // ends in ".md", not because of the ADS guard above. Kept as a
        // regression guard for the self-consistency the ADS fix restores.
        Click("file.md:stream").Action.ShouldBe(NavigationAction.Block);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("vbscript:x")]
    [InlineData("data:text/html,<script>x</script>")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-msdt:/id")]
    [InlineData("search-ms:query=x")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ftp://example.com/x")]
    public void Every_other_scheme_is_blocked(string href)
    {
        // Nothing is allowed by default, so a link carrying an unexpected or
        // hostile scheme is rejected rather than executed. mailto and ftp are
        // blocked too: harmless-looking, but neither is in the spec's table,
        // and an allowlist that grows by sympathy is not an allowlist.
        Click(href).Action.ShouldBe(NavigationAction.Block);
    }

    [Fact]
    public void The_virtual_host_is_blocked_as_a_navigation_target()
    {
        // note.local exists for subresource loads. A NAVIGATION to it would
        // leave the shell and its CSP behind.
        Click("https://note.local/x.md").Action.ShouldBe(NavigationAction.Block);
    }

    [Fact]
    public void A_trailing_dot_fqdn_still_matches_the_virtual_host()
    {
        // DNS treats "note.local." as the same name as "note.local"; the
        // guard must not miss it just because Uri does not normalise the
        // trailing dot away.
        Click("https://note.local./x.md").Action.ShouldBe(NavigationAction.Block);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_href_is_blocked_without_throwing(string? href)
        => Click(href).Action.ShouldBe(NavigationAction.Block);

    [Fact]
    public void A_bare_fragment_is_blocked_here_because_the_page_handles_it()
    {
        // The bridge script scrolls in-note anchors itself and never posts
        // them, so one reaching the host means something unexpected happened.
        Click("#section").Action.ShouldBe(NavigationAction.Block);
    }

    [Fact]
    public void Every_decision_carries_a_reason()
    {
        Click("javascript:alert(1)").Reason.ShouldNotBeNullOrWhiteSpace();
        Click("https://example.com").Reason.ShouldNotBeNullOrWhiteSpace();
        Click("other.md").Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_link_that_escapes_and_re_enters_is_still_blocked()
    {
        var target = Click("sub/../other.md").Target;

        target.ShouldBeNull("that link escapes and re-enters, and is blocked");
    }

    [Fact]
    public void The_initial_shell_load_is_allowed()
    {
        // Cancelling this one is how a note ends up permanently blank with no
        // error anywhere.
        NavigationPolicy.DecideNavigation("about:blank", shellLoaded: false)
            .Action.ShouldBe(NavigationAction.AllowShellLoad);
    }

    [Fact]
    public void A_second_about_blank_navigation_after_the_shell_loaded_is_blocked()
    {
        NavigationPolicy.DecideNavigation("about:blank", shellLoaded: true)
            .Action.ShouldBe(NavigationAction.Block);
    }

    [Fact]
    public void The_initial_shell_load_is_allowed_as_the_data_uri_a_real_runtime_reports()
    {
        // NavigateToString is itself implemented as a navigation to a
        // "data:text/html" URI built from the supplied HTML -- that is what
        // NavigationStarting's Uri actually carries on a live WebView2
        // runtime, not "about:blank". Cancelling it is how a note ends up
        // permanently blank with no error anywhere.
        NavigationPolicy.DecideNavigation(
                "data:text/html;charset=utf-8;base64,PGh0bWw+PC9odG1sPg==",
                shellLoaded: false)
            .Action.ShouldBe(NavigationAction.AllowShellLoad);
    }

    [Fact]
    public void A_second_shell_data_uri_navigation_after_the_shell_loaded_is_blocked()
    {
        NavigationPolicy.DecideNavigation(
                "data:text/html;charset=utf-8;base64,PGh0bWw+PC9odG1sPg==",
                shellLoaded: true)
            .Action.ShouldBe(NavigationAction.Block);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("file:///C:/x")]
    [InlineData("javascript:x")]
    [InlineData("about:config")]
    [InlineData("about:srcdoc")]
    [InlineData("data:text/plain,not-the-shell")]
    [InlineData(null)]
    public void Every_other_navigation_is_blocked_in_both_states(string? uri)
    {
        // The allowance is exact: "about:config" and "about:srcdoc" are
        // "about:"-prefixed too, but only "about:blank" is the shell's own
        // load, so both are blocked even at shellLoaded: false -- the
        // backstop is safe by construction, not merely because WebView2
        // happens never to send these here.
        NavigationPolicy.DecideNavigation(uri, shellLoaded: false)
            .Action.ShouldBe(NavigationAction.Block);
        NavigationPolicy.DecideNavigation(uri, shellLoaded: true)
            .Action.ShouldBe(NavigationAction.Block);
    }
}
