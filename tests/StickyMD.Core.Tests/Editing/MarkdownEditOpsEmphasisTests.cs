using Shouldly;
using StickyMD.Core.Editing;

namespace StickyMD.Core.Tests.Editing;

public class MarkdownEditOpsEmphasisTests
{
    [Fact]
    public void Bold_wraps_the_selection_and_keeps_it_selected()
    {
        var result = MarkdownEditOps.ToggleBold("hello", 0, 5);

        result.Text.ShouldBe("**hello**");
        result.SelectionStart.ShouldBe(2);
        result.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Bold_wraps_a_selection_in_the_middle_of_text()
    {
        var result = MarkdownEditOps.ToggleBold("say hello now", 4, 5);

        result.Text.ShouldBe("say **hello** now");
        result.SelectionStart.ShouldBe(6);
        result.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Bold_is_a_round_trip()
    {
        var once = MarkdownEditOps.ToggleBold("hello", 0, 5);

        var twice = MarkdownEditOps.ToggleBold(
            once.Text, once.SelectionStart, once.SelectionLength);

        twice.Text.ShouldBe("hello");
        twice.SelectionStart.ShouldBe(0);
        twice.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Bold_unwraps_when_markers_surround_the_selection()
    {
        var result = MarkdownEditOps.ToggleBold("say **hello** now", 6, 5);

        result.Text.ShouldBe("say hello now");
        result.SelectionStart.ShouldBe(4);
        result.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Bold_unwraps_when_markers_are_inside_the_selection()
    {
        var result = MarkdownEditOps.ToggleBold("say **hello** now", 4, 9);

        result.Text.ShouldBe("say hello now");
        result.SelectionStart.ShouldBe(4);
        result.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Bold_on_a_selection_that_partially_overlaps_markers_wraps_again()
    {
        // Selecting "*ab*" out of "**ab**" matches neither unwrap case, so it
        // wraps instead. The result is messy but never destructive -- no user
        // text is lost. Pinned deliberately: this documents CURRENT behaviour,
        // not ideal behaviour. If Plan B's editor snaps selections to marker
        // boundaries, revisit this expectation rather than assuming it is a bug.
        MarkdownEditOps.ToggleBold("**ab**", 1, 4)
            .Text.ShouldBe("****ab****");
    }

    [Fact]
    public void Bold_on_an_empty_selection_places_the_caret_between_markers()
    {
        var result = MarkdownEditOps.ToggleBold("", 0, 0);

        result.Text.ShouldBe("****");
        result.SelectionStart.ShouldBe(2);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Bold_on_an_empty_selection_mid_text_inserts_at_the_caret()
    {
        var result = MarkdownEditOps.ToggleBold("ab", 1, 0);

        result.Text.ShouldBe("a****b");
        result.SelectionStart.ShouldBe(3);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Italic_wraps_with_a_single_asterisk()
    {
        var result = MarkdownEditOps.ToggleItalic("hello", 0, 5);

        result.Text.ShouldBe("*hello*");
        result.SelectionStart.ShouldBe(1);
        result.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Italic_is_a_round_trip()
    {
        var once = MarkdownEditOps.ToggleItalic("hello", 0, 5);

        var twice = MarkdownEditOps.ToggleItalic(
            once.Text, once.SelectionStart, once.SelectionLength);

        twice.Text.ShouldBe("hello");
        twice.SelectionStart.ShouldBe(0);
        twice.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Italic_inside_bold_does_not_unwrap_the_bold()
    {
        // Selection is "hello" inside "**hello**". Italic must not see the
        // surrounding ** as its own single-asterisk markers.
        var result = MarkdownEditOps.ToggleItalic("**hello**", 2, 5);

        result.Text.ShouldBe("***hello***");
        result.SelectionStart.ShouldBe(3);
        result.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Null_text_is_treated_as_empty()
    {
        var result = MarkdownEditOps.ToggleBold(null!, 0, 0);

        result.Text.ShouldBe("****");
    }

    [Fact]
    public void Out_of_range_selection_is_clamped()
    {
        var result = MarkdownEditOps.ToggleBold("abc", 10, 10);

        result.Text.ShouldBe("abc****");
        result.SelectionStart.ShouldBe(5);
    }
}
