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
    public void A_sub_heading_is_a_title_when_there_is_no_h1()
        => NoteTitleResolver.Resolve("## Sub heading only\n\nbody", Path)
            .ShouldBe("Sub heading only");

    [Fact]
    public void A_markdown_table_does_not_become_the_title()
    {
        // The bug that killed the first-non-empty-line rule. The user renamed
        // the file to say what the note was, pasted a shortcuts table, and the
        // header started reading "|Shortcut|Action|".
        var table = "|Shortcut|Action|\n|---|---|\n|Ctrl+B|Split clip|\n";

        NoteTitleResolver.Resolve(table, Path).ShouldBe("my-file");
    }

    [Fact]
    public void Plain_prose_falls_back_to_the_filename()
        => NoteTitleResolver.Resolve("\n   just some text\nmore\n", Path)
            .ShouldBe("my-file");

    [Fact]
    public void A_list_does_not_become_the_title()
        => NoteTitleResolver.Resolve("- milk\n- eggs\n", Path).ShouldBe("my-file");

    [Fact]
    public void A_hashtag_is_not_a_heading()
        // No space after the '#', so CommonMark says this is not a heading and
        // neither do we -- otherwise "#todo" at the top of a note wins.
        => NoteTitleResolver.Resolve("#todo buy milk\n", Path).ShouldBe("my-file");

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
        // "=== not underline" is not a setext rule, so "Hello" is just prose
        // and the filename wins. This asserted "Hello" while the
        // first-non-empty-line rule existed, which made it pass for the wrong
        // reason: it could not tell a recognised setext heading from a line
        // the fallback happened to pick up.
        => NoteTitleResolver.Resolve("Hello\n=== not underline\n", Path)
            .ShouldBe("my-file");
}
