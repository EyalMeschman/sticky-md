using Shouldly;
using StickyMD.Core.Editing;

namespace StickyMD.Core.Tests.Editing;

public class MarkdownEditOpsListTests
{
    [Fact]
    public void Continues_a_dash_bullet()
    {
        var text = "- milk";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("- milk\n- ");
        result.SelectionStart.ShouldBe(result.Text.Length);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Continues_an_asterisk_bullet()
    {
        var text = "* milk";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("* milk\n* ");
        result.SelectionStart.ShouldBe(9);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Continues_a_plus_bullet()
    {
        var text = "+ milk";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("+ milk\n+ ");
        result.SelectionStart.ShouldBe(9);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Increments_an_ordered_marker()
    {
        var text = "1. first";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("1. first\n2. ");
        result.SelectionStart.ShouldBe(12);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Increments_from_an_arbitrary_ordered_number()
    {
        var text = "- a\n7. seventh";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("- a\n7. seventh\n8. ");
        result.SelectionStart.ShouldBe(18);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Preserves_indentation()
    {
        var text = "  - nested";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("  - nested\n  - ");
        result.SelectionStart.ShouldBe(15);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Continues_a_task_item_as_unchecked()
    {
        var text = "- [ ] buy milk";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("- [ ] buy milk\n- [ ] ");
        result.SelectionStart.ShouldBe(21);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Continues_a_checked_task_item_as_unchecked()
    {
        var text = "- [x] done thing";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("- [x] done thing\n- [ ] ");
        result.SelectionStart.ShouldBe(23);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Empty_bullet_terminates_the_list()
    {
        var text = "- milk\n- ";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("- milk\n");
        result.SelectionStart.ShouldBe(7);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Empty_indented_bullet_terminates_the_list_and_drops_the_indent()
    {
        var text = "- a\n  - ";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("- a\n");
        result.SelectionStart.ShouldBe(4);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Empty_task_item_terminates_the_list()
    {
        var text = "- [ ] a\n- [ ] ";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("- [ ] a\n");
        result.SelectionStart.ShouldBe(8);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Empty_ordered_item_terminates_the_list()
    {
        var text = "1. a\n2. ";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("1. a\n");
        result.SelectionStart.ShouldBe(5);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Non_list_line_is_returned_unchanged()
    {
        var text = "just prose";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe(text);
        result.SelectionStart.ShouldBe(text.Length);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Empty_document_is_returned_unchanged()
    {
        var result = MarkdownEditOps.ContinueList("", 0, 0);

        result.Text.ShouldBe("");
        result.SelectionStart.ShouldBe(0);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Continues_from_a_caret_in_the_middle_of_the_document()
    {
        var text = "- milk\ntrailing";

        var result = MarkdownEditOps.ContinueList(text, 6, 0);

        result.Text.ShouldBe("- milk\n- \ntrailing");
        result.SelectionStart.ShouldBe(9);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Deletes_a_non_empty_selection_before_continuing()
    {
        var text = "- milk and eggs";

        // Select " and eggs", press Enter.
        var result = MarkdownEditOps.ContinueList(text, 6, 9);

        result.Text.ShouldBe("- milk\n- ");
        result.SelectionStart.ShouldBe(9);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Handles_crlf_documents()
    {
        var text = "- milk\r\n- eggs";

        var result = MarkdownEditOps.ContinueList(text, text.Length, 0);

        result.Text.ShouldBe("- milk\r\n- eggs\n- ");
        result.SelectionStart.ShouldBe(17);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Caret_at_the_start_of_a_leading_newline_document_does_not_throw()
    {
        // Regression: LineStart used to return 1 here while LineEnd returned 0,
        // and text[1..0] threw. Caret is on an empty first line, which is not a
        // list line, so the input comes back unchanged.
        var result = MarkdownEditOps.ContinueList("\n", 0, 0);

        result.Text.ShouldBe("\n");
        result.SelectionStart.ShouldBe(0);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Caret_at_the_start_of_a_document_beginning_with_a_blank_line_does_not_throw()
    {
        var result = MarkdownEditOps.ContinueList("\nfoo", 0, 0);

        result.Text.ShouldBe("\nfoo");
        result.SelectionStart.ShouldBe(0);
        result.SelectionLength.ShouldBe(0);
    }
}
