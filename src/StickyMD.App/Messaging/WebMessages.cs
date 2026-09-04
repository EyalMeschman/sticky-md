using System.Text.Json;
using System.Text.Json.Serialization;
using StickyMD.Core.Markdown;

namespace StickyMD.App.Messaging;

/// <summary>What the host pushes into the page.</summary>
public sealed record RenderMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("html")] string Html,
    [property: JsonPropertyName("token")] string Token);

/// <summary>
/// What the page posts back, flattened into one shape. The four message types
/// share so few fields that separate records would buy nothing but a
/// discriminated parse.
/// </summary>
/// <param name="SpanStart">-1 when absent. Validated by TaskListToggler, not here.</param>
public sealed record InboundMessage(
    string Type,
    int SpanStart,
    int SpanEnd,
    string? Token,
    string? Href);

/// <summary>
/// The host-page message contract.
/// </summary>
/// <remarks>
/// The property names are camelCase because that is what the bridge script
/// reads. A PascalCase render message is not an error -- the script's
/// `message.type !== 'render'` check simply never matches, and every note
/// stays blank with nothing in any log. That is why the names are asserted in
/// tests rather than trusted to a serializer default.
/// </remarks>
public static class WebMessages
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Render(RenderResult result)
        => JsonSerializer.Serialize(
            new RenderMessage("render", result.Html, result.Token), Options);

    /// <summary>
    /// Parses a message from the page. Returns null for anything
    /// unrecognisable.
    /// </summary>
    /// <remarks>
    /// NEVER throws. WebMessageReceived is raised on the UI thread, and an
    /// unhandled exception there takes the process down -- so a malformed
    /// message from a renderer process must degrade to "ignored", not to a
    /// crash.
    /// </remarks>
    public static InboundMessage? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            if (!document.RootElement.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var typeName = type.GetString();
            if (string.IsNullOrEmpty(typeName)) return null;

            return new InboundMessage(
                typeName,
                ReadInt(document.RootElement, "spanStart"),
                ReadInt(document.RootElement, "spanEnd"),
                ReadString(document.RootElement, "token"),
                ReadString(document.RootElement, "href"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>-1 for absent or unusable, which no valid span can be.</summary>
    private static int ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return -1;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            // parseInt in the bridge yields a number, but tolerating a string
            // costs nothing and the span is validated downstream regardless.
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => -1,
        };
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
