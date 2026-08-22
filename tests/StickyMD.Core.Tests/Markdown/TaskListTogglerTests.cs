using Shouldly;
using StickyMD.Core.Markdown;

namespace StickyMD.Core.Tests.Markdown;

public class TaskListTogglerTests
{
    [Fact]
    public void Checks_an_unchecked_box()
    {
        var result = TaskListToggler.Toggle("- [ ] buy milk", 2, 4);

        result.Applied.ShouldBeTrue();
        result.Markdown.ShouldBe("- [x] buy milk");
    }

    [Fact]
    public void Unchecks_a_checked_box()
    {
        var result = TaskListToggler.Toggle("- [x] buy milk", 2, 4);

        result.Applied.ShouldBeTrue();
        result.Markdown.ShouldBe("- [ ] buy milk");
    }

    [Fact]
    public void Unchecks_an_uppercase_checked_box()
    {
        TaskListToggler.Toggle("- [X] buy milk", 2, 4)
            .Markdown.ShouldBe("- [ ] buy milk");
    }

    [Fact]
    public void Toggling_twice_returns_the_original()
    {
        var once = TaskListToggler.Toggle("- [ ] a", 2, 4);

        TaskListToggler.Toggle(once.Markdown, 2, 4).Markdown.ShouldBe("- [ ] a");
    }

    [Fact]
    public void Changes_only_the_targeted_box()
    {
        const string markdown = "- [ ] one\n- [ ] two\n- [ ] three\n";
        var second = markdown.IndexOf("[ ] two", StringComparison.Ordinal);

        var result = TaskListToggler.Toggle(markdown, second, second + 2);

        result.Markdown.ShouldBe("- [ ] one\n- [x] two\n- [ ] three\n");
    }

    [Fact]
    public void Leaves_the_rest_of_the_document_byte_identical()
    {
        const string markdown = "# T\r\n\r\n- [ ] a\r\n\r\n> quote\r\n\r\n```\ncode\n```\r\n";
        var span = markdown.IndexOf("[ ]", StringComparison.Ordinal);

        var result = TaskListToggler.Toggle(markdown, span, span + 2);

        result.Markdown.Length.ShouldBe(markdown.Length);
        result.Markdown.Remove(span, 3).ShouldBe(markdown.Remove(span, 3));
    }

    [Fact]
    public void Rejects_a_span_that_is_not_a_checkbox()
    {
        const string markdown = "- [ ] buy milk";

        var result = TaskListToggler.Toggle(markdown, 6, 8);

        result.Applied.ShouldBeFalse();
        result.Markdown.ShouldBe(markdown);
        result.Reason.ShouldNotBeNull();
    }

    [Fact]
    public void Rejects_a_span_of_the_wrong_length()
    {
        const string markdown = "- [ ] buy milk";

        TaskListToggler.Toggle(markdown, 2, 5).Applied.ShouldBeFalse();
        TaskListToggler.Toggle(markdown, 2, 3).Applied.ShouldBeFalse();
    }

    [Fact]
    public void Rejects_a_span_past_the_end_of_the_document()
    {
        const string markdown = "- [ ] a";

        var result = TaskListToggler.Toggle(markdown, 100, 102);

        result.Applied.ShouldBeFalse();
        result.Markdown.ShouldBe(markdown);
    }

    [Fact]
    public void Rejects_a_negative_span()
        => TaskListToggler.Toggle("- [ ] a", -1, 1).Applied.ShouldBeFalse();

    [Fact]
    public void Rejects_an_inverted_span()
        => TaskListToggler.Toggle("- [ ] a", 4, 2).Applied.ShouldBeFalse();

    [Fact]
    public void Rejects_null_markdown()
        => TaskListToggler.Toggle(null!, 2, 4).Applied.ShouldBeFalse();

    [Fact]
    public void Rejects_a_bracket_pair_that_is_not_a_task_marker()
    {
        // "[y]" is not a checkbox, so a span pointing at it must be refused.
        const string markdown = "- [y] weird";

        TaskListToggler.Toggle(markdown, 2, 4).Applied.ShouldBeFalse();
    }

    [Fact]
    public void End_to_end_a_rendered_span_toggles_the_right_box()
    {
        // The proof that Task 16 and Task 17 agree on the span convention.
        const string markdown = "# Shopping\n\n- [ ] milk\n- [ ] eggs\n";
        var rendered = new MarkdownRenderer()
            .Render(markdown, new RenderOptions(AllowRemoteImages: false));

        var spans = System.Text.RegularExpressions.Regex
            .Matches(rendered.Html,
                @"data-span-start=""(\d+)""\s+data-span-end=""(\d+)""")
            .Select(m => (Start: int.Parse(m.Groups[1].Value),
                          End: int.Parse(m.Groups[2].Value)))
            .ToList();

        spans.Count.ShouldBe(2);

        var result = TaskListToggler.Toggle(markdown, spans[1].Start, spans[1].End);

        result.Applied.ShouldBeTrue();
        result.Markdown.ShouldBe("# Shopping\n\n- [ ] milk\n- [x] eggs\n");
    }
}
