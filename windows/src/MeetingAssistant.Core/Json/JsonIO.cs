using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeetingAssistant.Core.Json;

/// <summary>
/// Reads and writes the app's JSON files in the same format as the Mac app (spec 01-recording,
/// "JSON encoding rules"): UTF-8, pretty-printed with sorted keys, ISO 8601 UTC dates without
/// fractional seconds, nulls omitted, atomic writes.
/// </summary>
public static class JsonIO
{
    /// Options for (de)serialising model types: camelCase names, enums as camelCase strings,
    /// nulls omitted, Mac-compatible dates.
    public static readonly JsonSerializerOptions Options = CreateOptions();

    static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        o.Converters.Add(new IsoDateConverter());
        o.Converters.Add(new NullableIsoDateConverter());
        return o;
    }

    /// Serialise to the Mac's pretty format: two-space indent, `"key" : value`, keys sorted.
    public static string Serialize<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, Options);
        return Format(node);
    }

    /// Format any node in the Mac's pretty format with sorted keys.
    public static string Format(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteNode(sb, node, 0);
        return sb.ToString();
    }

    /// Compact form with sorted keys (used in prompts, like the Mac's `compactJSON`).
    public static string Compact(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteCompact(sb, node);
        return sb.ToString();
    }

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Empty JSON");

    public static T Read<T>(string path) => Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));

    public static bool TryRead<T>(string path, out T? value)
    {
        try { value = Read<T>(path); return true; }
        catch { value = default; return false; }
    }

    public static void Write<T>(T value, string path) => WriteText(path, Serialize(value));

    /// Atomic write: temp file in the same folder, then replace.
    public static void WriteText(string path, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    // MARK: formatting

    static void WriteNode(StringBuilder sb, JsonNode? node, int indent)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                {
                    var keys = obj.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
                    if (keys.Count == 0) { sb.Append("{\n\n").Append(' ', indent).Append('}'); break; }
                    sb.Append("{\n");
                    for (int i = 0; i < keys.Count; i++)
                    {
                        sb.Append(' ', indent + 2).Append(EscapeSlashes(Quote(keys[i]))).Append(" : ");
                        WriteNode(sb, obj[keys[i]], indent + 2);
                        if (i < keys.Count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(' ', indent).Append('}');
                    break;
                }
            case JsonArray arr:
                {
                    if (arr.Count == 0) { sb.Append("[\n\n").Append(' ', indent).Append(']'); break; }
                    sb.Append("[\n");
                    for (int i = 0; i < arr.Count; i++)
                    {
                        sb.Append(' ', indent + 2);
                        WriteNode(sb, arr[i], indent + 2);
                        if (i < arr.Count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(' ', indent).Append(']');
                    break;
                }
            case JsonValue v:
                sb.Append(EscapeSlashes(ScalarText(v)));
                break;
        }
    }

    static void WriteCompact(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonObject obj:
                sb.Append('{');
                bool first = true;
                foreach (var k in obj.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Quote(k)).Append(':');
                    WriteCompact(sb, obj[k]);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (int i = 0; i < arr.Count; i++) { if (i > 0) sb.Append(','); WriteCompact(sb, arr[i]); }
                sb.Append(']');
                break;
            case JsonValue v: sb.Append(ScalarText(v)); break;
        }
    }

    static readonly JsonSerializerOptions ScalarOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    static string ScalarText(JsonValue v)
    {
        if (v.TryGetValue<double>(out var d) && v.GetValueKind() == JsonValueKind.Number) return FormatNumber(d);
        if (v.GetValueKind() == JsonValueKind.String) return Quote(v.GetValue<string>());
        return v.ToJsonString(ScalarOptions);
    }

    /// Numbers like Swift's encoder: integral values without a decimal point, others shortest round-trip.
    public static string FormatNumber(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture).Replace("E", "e");
    }

    /// The Mac's file encoder (`JSONIO`, without `.withoutEscapingSlashes`) writes "/" as "\/" in
    /// strings; the compact prompt form does not. A "/" can only occur inside JSON strings.
    static string EscapeSlashes(string json) => json.Contains('/') ? json.Replace("/", "\\/") : json;

    /// Like Swift's JSONEncoder: only `"`, `\` and control characters are escaped; everything else
    /// (emoji, narrow no-break space, …) is written as UTF-8. Slashes are handled by EscapeSlashes.
    static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}

/// ISO 8601 UTC dates without fractional seconds (`2026-11-10T13:03:22Z`). Reading also accepts
/// fractional seconds and offsets.
public sealed class IsoDateConverter : JsonConverter<DateTime>
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            // Swift's default (reference date 2001-01-01) is never written by the app, but be lenient.
            return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(reader.GetDouble());
        }
        var s = reader.GetString() ?? throw new JsonException("date expected");
        return Parse(s);
    }

    public static DateTime Parse(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime;

    public static string ToText(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return utc.ToString(Format, CultureInfo.InvariantCulture);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToText(value));
}

public sealed class NullableIsoDateConverter : JsonConverter<DateTime?>
{
    static readonly IsoDateConverter Inner = new();
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(DateTime), options);
    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue(); else Inner.Write(writer, value.Value, options);
    }
}
