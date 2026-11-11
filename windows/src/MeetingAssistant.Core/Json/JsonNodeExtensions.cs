using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeetingAssistant.Core.Json;

/// Helpers for free-form JSON (Aya records, Claude output), the counterpart of the Mac's `JSONValue`.
public static class JsonNodeExtensions
{
    public static string? AsString(this JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static double? AsNumber(this JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    public static bool? AsBool(this JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;

    public static JsonArray? AsArray(this JsonNode? n) => n as JsonArray;
    public static JsonObject? AsObject(this JsonNode? n) => n as JsonObject;

    /// True for null, blank strings, [] and {}.
    public static bool IsEmptyValue(this JsonNode? n) => n switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetValue<string>()),
        JsonValue v when v.GetValueKind() == JsonValueKind.Null => true,
        _ => false,
    };

    /// Plain text for prompts and the UI: strings as-is, integral numbers without ".0",
    /// booleans as yes/no, arrays joined with ", ", objects as compact JSON, null as "".
    public static string Describe(this JsonNode? n) => n switch
    {
        null => "",
        JsonArray a => string.Join(", ", a.Select(x => x.Describe())),
        JsonObject => JsonIO.Compact(n),
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => DescribeNumber(v.GetValue<double>()),
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            _ => "",
        },
        _ => "",
    };

    static string DescribeNumber(double d) =>
        d == Math.Round(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);

    public static JsonNode? DeepCopy(this JsonNode? n) => n?.DeepClone();

    public static JsonNode? ParseNode(string text) => JsonNode.Parse(text);
}
