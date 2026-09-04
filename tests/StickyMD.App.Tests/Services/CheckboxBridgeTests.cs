using Shouldly;
using StickyMD.App.Services;
using StickyMD.Core.Markdown;

namespace StickyMD.App.Tests.Services;

public class CheckboxBridgeTests
{
    private const string Buffer = "- [ ] first\n- [x] second\n";

    private static string TokenFor(string markdown)
        => MarkdownRenderer.ComputeToken(markdown);

    /// <summary>
    /// The span of the "[ ]" token in "- [ ] first": '[' is at index 2 and ']'
    /// at index 4. Markdig's SourceSpan.End is INCLUSIVE, so End is 4 and the
    /// span covers three characters -- verified empirically three times in Plan
    /// A, and it holds for CJK, accented text, and emoji.
    /// </summary>
    private const int FirstStart = 2;
    private const int FirstEnd = 4;

    [Fact]
    public void A_matching_token_and_a_valid_span_toggles_the_box()
    {
        var decision = CheckboxBridge.Decide(
            Buffer, FirstStart, FirstEnd, TokenFor(Buffer));

        decision.Verdict.ShouldBe(ToggleVerdict.Apply);
        decision.Markdown.ShouldBe("- [x] first\n- [x] second\n");
    }

    [Fact]
    public void Toggling_the_second_box_leaves_the_first_alone()
    {
        // "- [x] second" starts at index 12; '[' is at 14, ']' at 16.
        var decision = CheckboxBridge.Decide(Buffer, 14, 16, TokenFor(Buffer));

        decision.Verdict.ShouldBe(ToggleVerdict.Apply);
        decision.Markdown.ShouldBe("- [ ] first\n- [ ] second\n");
    }

    [Fact]
    public void A_stale_token_drops_the_click_and_leaves_the_buffer_untouched()
    {
        var decision = CheckboxBridge.Decide(
            Buffer, FirstStart, FirstEnd, TokenFor("something else entirely"));

        decision.Verdict.ShouldBe(ToggleVerdict.StaleToken);
        decision.Markdown.ShouldBe(Buffer);
        decision.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_click_carrying_no_token_is_dropped()
    {
        CheckboxBridge.Decide(Buffer, FirstStart, FirstEnd, null)
            .Verdict.ShouldBe(ToggleVerdict.StaleToken);
    }

    [Fact]
    public void An_off_by_one_span_is_refused_rather_than_writing_the_wrong_characters()
    {
        // This is the test that catches an EXCLUSIVE-end misreading. A span of
        // 2..5 covers four characters, which is not a task marker, so the
        // toggler refuses it -- and the buffer is unchanged rather than
        // silently mangled.
        var decision = CheckboxBridge.Decide(Buffer, 2, 5, TokenFor(Buffer));

        decision.Verdict.ShouldBe(ToggleVerdict.InvalidSpan);
        decision.Markdown.ShouldBe(Buffer);
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(2, 1)]
    [InlineData(1000, 1002)]
    public void A_nonsensical_span_is_refused(int start, int end)
    {
        CheckboxBridge.Decide(Buffer, start, end, TokenFor(Buffer))
            .Verdict.ShouldBe(ToggleVerdict.InvalidSpan);
    }

    [Fact]
    public void A_span_pointing_at_ordinary_text_is_refused()
    {
        // "fir" rather than "[ ]".
        CheckboxBridge.Decide(Buffer, 6, 8, TokenFor(Buffer))
            .Verdict.ShouldBe(ToggleVerdict.InvalidSpan);
    }

    [Fact]
    public void An_uppercase_X_toggles_off()
    {
        const string upper = "- [X] shouty\n";

        var decision = CheckboxBridge.Decide(upper, 2, 4, TokenFor(upper));

        decision.Verdict.ShouldBe(ToggleVerdict.Apply);
        decision.Markdown.ShouldBe("- [ ] shouty\n");
    }

    [Fact]
    public void Toggling_twice_returns_the_original_text_exactly()
    {
        var once = CheckboxBridge.Decide(
            Buffer, FirstStart, FirstEnd, TokenFor(Buffer)).Markdown;

        var twice = CheckboxBridge.Decide(
            once, FirstStart, FirstEnd, TokenFor(once)).Markdown;

        twice.ShouldBe(Buffer);
    }

    [Fact]
    public void The_token_of_the_result_differs_from_the_token_of_the_input()
    {
        // The next click must carry the NEW token, or the second toggle on a
        // note is always dropped as stale.
        var result = CheckboxBridge.Decide(
            Buffer, FirstStart, FirstEnd, TokenFor(Buffer)).Markdown;

        TokenFor(result).ShouldNotBe(TokenFor(Buffer));
    }

    [Fact]
    public void A_span_beyond_a_shrunken_buffer_is_refused_not_applied()
    {
        // The token guard normally catches this. If a buffer somehow shrank
        // while keeping its token, the span check is the second line of
        // defence -- and the claimed failure mode is "ignores a click", never
        // "edits the wrong task".
        const string shrunk = "- [ ]";

        CheckboxBridge.Decide(shrunk, 20, 22, TokenFor(shrunk))
            .Verdict.ShouldBe(ToggleVerdict.InvalidSpan);
    }

    [Fact]
    public void A_span_over_a_checkbox_after_an_emoji_still_resolves()
    {
        // Spans are UTF-16 code unit offsets and an emoji is a surrogate pair,
        // so this is where an off-by-one in span handling shows up. Verified
        // in Plan A for CJK, accented text, and emoji.
        const string emoji = "- \U0001F600 note\n- [ ] task\n";

        var start = emoji.IndexOf("[ ]", StringComparison.Ordinal);

        var decision = CheckboxBridge.Decide(
            emoji, start, start + 2, TokenFor(emoji));

        decision.Verdict.ShouldBe(ToggleVerdict.Apply);
        decision.Markdown.ShouldContain("- [x] task");
        decision.Markdown.ShouldContain("\U0001F600");
    }
}
