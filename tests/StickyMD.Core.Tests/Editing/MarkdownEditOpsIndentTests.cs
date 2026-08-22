using Shouldly;
using StickyMD.Core.Editing;

namespace StickyMD.Core.Tests.Editing;

public class MarkdownEditOpsIndentTests
{
    [Fact]
    public void Indent_shifts_a_single_list_line()
    {
        var text = "- milk";

        var result = MarkdownEditOps.Indent(text, 6, 0);

        result.Text.ShouldBe("  - milk");
        result.SelectionStart.ShouldBe(8);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Indent_shifts_every_selected_list_line()
    {
        var text = "- a\n- b\n- c";

        // Select from inside line 1 through inside line 3.
        var result = MarkdownEditOps.Indent(text, 2, 7);

        result.Text.ShouldBe("  - a\n  - b\n  - c");
        result.SelectionStart.ShouldBe(4);
        result.SelectionLength.ShouldBe(11);
    }

    [Fact]
    public void Indent_leaves_untouched_lines_alone()
    {
        var text = "- a\n- b\n- c";

        var result = MarkdownEditOps.Indent(text, 0, 3);

        result.Text.ShouldBe("  - a\n- b\n- c");
        result.SelectionStart.ShouldBe(0);
        result.SelectionLength.ShouldBe(5);
    }

    [Fact]
    public void Indent_on_a_non_list_line_inserts_two_spaces_at_the_caret()
    {
        var text = "prose here";

        var result = MarkdownEditOps.Indent(text, 5, 0);

        result.Text.ShouldBe("prose   here");
        result.SelectionStart.ShouldBe(7);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Indent_preserves_an_already_indented_item()
    {
        var text = "  - nested";

        var result = MarkdownEditOps.Indent(text, 10, 0);

        result.Text.ShouldBe("    - nested");
        result.SelectionStart.ShouldBe(12);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Indent_handles_task_items()
    {
        var text = "- [ ] buy milk";

        var result = MarkdownEditOps.Indent(text, 14, 0);

        result.Text.ShouldBe("  - [ ] buy milk");
        result.SelectionStart.ShouldBe(16);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Indent_handles_ordered_items()
    {
        var text = "1. first";

        var result = MarkdownEditOps.Indent(text, 8, 0);

        result.Text.ShouldBe("  1. first");
        result.SelectionStart.ShouldBe(10);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Outdent_removes_two_leading_spaces()
    {
        var text = "  - nested";

        var result = MarkdownEditOps.Outdent(text, 10, 0);

        result.Text.ShouldBe("- nested");
        result.SelectionStart.ShouldBe(8);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Outdent_at_column_zero_is_a_no_op()
    {
        var text = "- milk";

        var result = MarkdownEditOps.Outdent(text, 6, 0);

        result.Text.ShouldBe("- milk");
        result.SelectionStart.ShouldBe(6);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Outdent_removes_only_one_space_when_only_one_exists()
    {
        var text = " - milk";

        var result = MarkdownEditOps.Outdent(text, 7, 0);

        result.Text.ShouldBe("- milk");
        result.SelectionStart.ShouldBe(6);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Outdent_shifts_every_selected_line()
    {
        var text = "  - a\n  - b";

        var result = MarkdownEditOps.Outdent(text, 0, text.Length);

        result.Text.ShouldBe("- a\n- b");
        result.SelectionStart.ShouldBe(0);
        result.SelectionLength.ShouldBe(7);
    }

    [Fact]
    public void Outdent_on_a_non_list_line_removes_leading_spaces()
    {
        var text = "    prose";

        var result = MarkdownEditOps.Outdent(text, 9, 0);

        result.Text.ShouldBe("  prose");
        result.SelectionStart.ShouldBe(7);
        result.SelectionLength.ShouldBe(0);
    }

    [Fact]
    public void Indent_then_outdent_is_a_round_trip()
    {
        var text = "- a\n- b";

        var indented = MarkdownEditOps.Indent(text, 0, text.Length);
        var back = MarkdownEditOps.Outdent(
            indented.Text, indented.SelectionStart, indented.SelectionLength);

        back.Text.ShouldBe(text);
        back.SelectionStart.ShouldBe(0);
        back.SelectionLength.ShouldBe(7);
    }

    [Fact]
    public void Indent_extends_a_multi_line_selection_to_cover_inserted_spaces()
    {
        var text = "- a\n- b";

        var result = MarkdownEditOps.Indent(text, 0, 7);

        result.Text.ShouldBe("  - a\n  - b");
        result.SelectionStart.ShouldBe(0);
        result.SelectionLength.ShouldBe(11);
    }

    [Fact]
    public void Empty_document_is_unchanged()
    {
        var indented = MarkdownEditOps.Indent("", 0, 0);
        indented.Text.ShouldBe("  ");
        indented.SelectionStart.ShouldBe(2);
        indented.SelectionLength.ShouldBe(0);

        var outdented = MarkdownEditOps.Outdent("", 0, 0);
        outdented.Text.ShouldBe("");
        outdented.SelectionStart.ShouldBe(0);
        outdented.SelectionLength.ShouldBe(0);
    }
}
