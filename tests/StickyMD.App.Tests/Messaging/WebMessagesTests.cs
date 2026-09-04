using Shouldly;
using StickyMD.App.Messaging;
using StickyMD.Core.Markdown;

namespace StickyMD.App.Tests.Messaging;

public class WebMessagesTests
{
    [Fact]
    public void The_render_message_uses_the_names_the_bridge_script_reads()
    {
        // The script reads message.type, message.html and message.token.
        // PascalCase here would make every render silently do nothing.
        var json = WebMessages.Render(new RenderResult("<p>hi</p>", "ABC123"));

        json.ShouldContain("\"type\":\"render\"");
        json.ShouldContain("\"html\":");
        json.ShouldContain("\"token\":\"ABC123\"");
    }

    [Fact]
    public void The_render_message_escapes_html_safely_for_json()
    {
        var json = WebMessages.Render(
            new RenderResult("<p>a \"quoted\" \\ backslash</p>", "T"));

        // Round-trips: whatever this produces must parse back to the original.
        var parsed = System.Text.Json.JsonDocument.Parse(json);
        parsed.RootElement.GetProperty("html").GetString()
            .ShouldBe("<p>a \"quoted\" \\ backslash</p>");
    }

    [Fact]
    public void A_toggleTask_message_parses_with_its_span_and_token()
    {
        var message = WebMessages.Parse(
            """{"type":"toggleTask","spanStart":2,"spanEnd":4,"token":"ABC"}""");

        message.ShouldNotBeNull();
        message.Type.ShouldBe("toggleTask");
        message.SpanStart.ShouldBe(2);
        message.SpanEnd.ShouldBe(4);
        message.Token.ShouldBe("ABC");
    }

    [Fact]
    public void A_link_message_parses_with_its_raw_href()
    {
        var message = WebMessages.Parse("""{"type":"link","href":"other.md"}""");

        message!.Type.ShouldBe("link");
        message.Href.ShouldBe("other.md");
    }

    [Fact]
    public void A_requestEdit_message_parses()
        => WebMessages.Parse("""{"type":"requestEdit"}""")!.Type.ShouldBe("requestEdit");

    [Fact]
    public void A_ready_message_parses()
        => WebMessages.Parse("""{"type":"ready"}""")!.Type.ShouldBe("ready");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a bare string\"")]
    [InlineData("{}")]
    public void Anything_unrecognisable_parses_to_null_rather_than_throwing(string json)
    {
        // These arrive from a renderer process. A parse failure must never
        // reach the message handler as an exception -- WebMessageReceived runs
        // on the UI thread and an unhandled throw there takes down the app.
        WebMessages.Parse(json).ShouldBeNull();
    }

    [Fact]
    public void A_toggleTask_missing_its_span_parses_to_a_message_with_an_invalid_span()
    {
        // Parsing succeeds; the span is then rejected by TaskListToggler,
        // which is the component that owns span validation. Two places
        // validating spans is how they end up disagreeing.
        var message = WebMessages.Parse("""{"type":"toggleTask","token":"ABC"}""");

        message.ShouldNotBeNull();
        message.SpanStart.ShouldBeLessThan(0);
    }

    [Fact]
    public void A_span_arriving_as_a_json_string_still_parses()
    {
        // parseInt in the bridge yields a number, but a hand-crafted or future
        // message might not. Tolerating it costs nothing and the span is
        // validated downstream anyway.
        var message = WebMessages.Parse(
            """{"type":"toggleTask","spanStart":"2","spanEnd":"4","token":"A"}""");

        message!.SpanStart.ShouldBe(2);
        message.SpanEnd.ShouldBe(4);
    }
}
