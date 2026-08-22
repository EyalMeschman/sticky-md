using Shouldly;
using StickyMD.Core.Notes;

namespace StickyMD.Core.Tests.Notes;

public class NoteTitleResolverTests
{
    private const string Path = @"C:\notes\my-file.md";

    [Fact]
    public void Uses_first_atx_h1()
        => NoteTitleResolver.Resolve("# Hello world\n\nbody", Path)
            .ShouldBe("Hello world");

    [Fact]
    public void Skips_leading_blank_lines_before_h1()
        => NoteTitleResolver.Resolve("\n\n# Hello\n", Path).ShouldBe("Hello");

    [Fact]
    public void Trims_trailing_hashes_from_closed_atx_heading()
        => NoteTitleResolver.Resolve("# Hello ###\n", Path).ShouldBe("Hello");

    [Fact]
    public void Uses_setext_h1()
        => NoteTitleResolver.Resolve("Hello world\n===========\n\nbody", Path)
            .ShouldBe("Hello world");

    [Fact]
    public void Earlier_setext_h1_beats_a_later_atx_heading()
    {
        // The first heading positionally in the document wins, regardless of type (ATX vs setext).
        // This is intentional: for a sticky note, the first visible title is more natural than
        // strict ATX priority, which might select a lower sub-heading while ignoring a setext
        // title on the visible first line.
        NoteTitleResolver.Resolve("Title\n=====\n\n# Heading\n", Path)
            .ShouldBe("Title");
    }

    [Fact]
    public void Ignores_headings_inside_fenced_code()
        => NoteTitleResolver.Resolve("```\n# not a title\n```\n# Real title\n", Path)
            .ShouldBe("Real title");

    [Fact]
    public void Ignores_headings_inside_tilde_fenced_code()
        => NoteTitleResolver.Resolve("~~~\n# nope\n~~~\n# Yes\n", Path)
            .ShouldBe("Yes");

    [Fact]
    public void Falls_back_to_first_non_empty_line_stripping_hashes()
        => NoteTitleResolver.Resolve("## Sub heading only\n\nbody", Path)
            .ShouldBe("Sub heading only");

    [Fact]
    public void Falls_back_to_first_non_empty_line_for_plain_text()
        => NoteTitleResolver.Resolve("\n   just some text\nmore\n", Path)
            .ShouldBe("just some text");

    [Fact]
    public void Truncates_long_fallback_to_60_chars()
    {
        var line = new string('x', 100);

        NoteTitleResolver.Resolve(line, Path).Length.ShouldBe(60);
    }

    [Fact]
    public void Does_not_truncate_a_real_heading()
    {
        var heading = new string('y', 100);

        NoteTitleResolver.Resolve("# " + heading, Path).ShouldBe(heading);
    }

    [Fact]
    public void Falls_back_to_filename_when_content_is_empty()
        => NoteTitleResolver.Resolve("", Path).ShouldBe("my-file");

    [Fact]
    public void Falls_back_to_filename_when_content_is_whitespace()
        => NoteTitleResolver.Resolve("\n\t  \n \n", Path).ShouldBe("my-file");

    [Fact]
    public void Handles_crlf_line_endings()
        => NoteTitleResolver.Resolve("# Hello\r\n\r\nbody\r\n", Path).ShouldBe("Hello");

    [Fact]
    public void Setext_underline_must_be_all_equals()
        => NoteTitleResolver.Resolve("Hello\n=== not underline\n", Path)
            .ShouldBe("Hello");
}
