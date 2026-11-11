using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MeetingAssistant.Core.Json;

namespace MeetingAssistant.Core.Aya;

/// <summary>A workspace of the organization (spec 02 §8.2).</summary>
public sealed class AyaWorkspace
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("localizedLabel")] public Dictionary<string, string>? LocalizedLabel { get; set; }
    [JsonPropertyName("isPrivate")] public bool? IsPrivate { get; set; }
    /// The label in the UI language, else the slug, else the id.
    [JsonIgnore] public string Label => AyaText.Localized(LocalizedLabel) ?? Slug ?? Id;
}

/// <summary>A record type (list) of a workspace (spec 02 §8.2). <see cref="WorkspaceId"/> is its home workspace.</summary>
public sealed class AyaRecordSpec
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("techName")] public string TechName { get; set; } = "";
    [JsonPropertyName("workspaceId")] public string? WorkspaceId { get; set; }
    [JsonPropertyName("localizedLabel")] public Dictionary<string, string>? LocalizedLabel { get; set; }
    [JsonPropertyName("isSystem")] public bool? IsSystem { get; set; }
    [JsonIgnore] public string Label => AyaText.Localized(LocalizedLabel) ?? TechName;
}

/// <summary>
/// One record: a flat object of <c>id</c>, <c>computedTitle</c>, <c>&lt;field&gt;__value</c>, <c>&lt;rel&gt;__id(s)</c>, … and
/// system columns (spec 02 §8.2). Serialised as that flat object.
/// </summary>
[JsonConverter(typeof(AyaRecordConverter))]
public sealed class AyaRecord
{
    public JsonObject Values { get; }
    public AyaRecord(JsonObject? values = null) { Values = values ?? new JsonObject(); }

    /// Builds a record from key/value pairs (tests and fakes).
    public static AyaRecord Of(params (string Key, JsonNode? Value)[] values)
    {
        var o = new JsonObject();
        foreach (var (k, v) in values) o[k] = v?.DeepClone();
        return new AyaRecord(o);
    }

    public JsonNode? this[string key] => Values.TryGetPropertyValue(key, out var v) ? v : null;
    /// True when the key is present (even with a null value).
    public bool Has(string key) => Values.ContainsKey(key);

    public string Id => this["id"].AsString() ?? "";
    /// <c>computedTitle</c>, trimmed, or the id when empty.
    public string Title
    {
        get
        {
            var t = this["computedTitle"].AsString()?.Trim() ?? "";
            return t.Length == 0 ? Id : t;
        }
    }
    public DateTime? UpdatedAt => this["updatedAt"].AsString() is { } s ? AyaText.ParseDate(s) : null;
    public DateTime? CreatedAt => this["createdAt"].AsString() is { } s ? AyaText.ParseDate(s) : null;
    /// A date field's value.
    public DateTime? Date(string key) => AyaText.Date(this[key].AsString());

    /// The record a relation field points to: <c>x__id</c>, or the first of <c>x__ids</c>.
    public string? LinkedID(string key)
    {
        if (this[key].AsString() is { Length: > 0 } s) return s;
        return (this[key] as JsonArray)?.FirstOrDefault().AsString();
    }

    /// All records a relation field points to.
    public List<string> LinkedIDs(string key)
    {
        if (this[key].AsString() is { Length: > 0 } s) return new List<string> { s };
        return (this[key] as JsonArray)?.Select(x => x.AsString()).Where(x => x is not null).Select(x => x!).ToList() ?? new List<string>();
    }

    /// The title Aya shows for a linked record (<c>x__computed_title</c>, or the joined <c>x__computed_titles</c>).
    public string? LinkedTitle(string key)
    {
        var b = key.Split("__")[0];
        if (this[b + "__computed_title"].AsString() is { Length: > 0 } s) return s;
        var many = (this[b + "__computed_titles"] as JsonArray)?.Select(x => x.AsString()).Where(x => x is not null).ToList() ?? new();
        return many.Count == 0 ? null : string.Join(", ", many);
    }

    /// A copy with only the given keys.
    public AyaRecord Only(Func<string, bool> keep)
    {
        var o = new JsonObject();
        foreach (var (k, v) in Values) if (keep(k)) o[k] = v?.DeepClone();
        return new AyaRecord(o);
    }

    public AyaRecord Clone() => new((JsonObject)Values.DeepClone());
}

/// Reads and writes an <see cref="AyaRecord"/> as its flat JSON object.
public sealed class AyaRecordConverter : JsonConverter<AyaRecord>
{
    public override AyaRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("record object expected");
        return new AyaRecord(node);
    }

    public override void Write(Utf8JsonWriter writer, AyaRecord value, JsonSerializerOptions options) => value.Values.WriteTo(writer);
}

/// <summary>Text helpers for Aya: localized labels, dates (spec 02 §8.2).</summary>
public static class AyaText
{
    /// <summary>For tests: overrides the UI language used for labels.</summary>
    public static string? LanguageOverride { get; set; }

    /// The label in the system language, then English, then German, then the alphabetically first value.
    public static string? Localized(IReadOnlyDictionary<string, string>? map)
    {
        if (map is null || map.Count == 0) return null;
        var lang = LanguageOverride ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (map.TryGetValue(lang, out var v)) return v;
        if (map.TryGetValue("en", out v)) return v;
        if (map.TryGetValue("de", out v)) return v;
        return map.Values.OrderBy(x => x, StringComparer.Ordinal).First();
    }

    static readonly Regex InternetDate = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})$", RegexOptions.Compiled);

    /// ISO 8601 with a time zone, with or without fractional seconds. Returned in UTC.
    public static DateTime? ParseDate(string s)
    {
        if (!InternetDate.IsMatch(s)) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.UtcDateTime : null;
    }

    /// ISO 8601, else the first 10 characters as <c>yyyy-MM-dd</c> (local midnight).
    public static DateTime? Date(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (ParseDate(s) is { } d) return d;
        var head = s.Length > 10 ? s[..10] : s;
        return DateTime.TryParseExact(head, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var x)
            ? x.ToUniversalTime() : null;
    }

    /// ISO 8601 without fractional seconds, UTC (<c>2026-11-10T09:00:00Z</c>).
    public static string Iso(DateTime d) => IsoDateConverter.ToText(d);
}

/// <summary>The kinds of Aya failures (spec 02 §8.3).</summary>
public enum AyaErrorKind { NotConfigured, Unauthorized, Forbidden, NotFound, CreditsExhausted, BadRequest, Server, Network, Decoding }

/// <summary>An Aya failure. <see cref="Exception.Message"/> is the user-facing text (spec 02 §8.3).</summary>
public sealed class AyaException : Exception
{
    public AyaErrorKind Kind { get; }
    public string Detail { get; }
    public int StatusCode { get; }

    public AyaException(AyaErrorKind kind, string detail = "", int statusCode = 0) : base(Describe(kind, detail, statusCode))
    {
        Kind = kind; Detail = detail; StatusCode = statusCode;
    }

    public static string Describe(AyaErrorKind kind, string m, int code) => kind switch
    {
        AyaErrorKind.NotConfigured => "Aya is not connected yet. Enter the organization id and API key in Settings › Assist.",
        AyaErrorKind.Unauthorized => "Aya rejected the API key. Check it in Settings › Assist.",
        AyaErrorKind.Forbidden => $"No permission in Aya for this list or field. {m}",
        AyaErrorKind.NotFound => $"Not found in Aya (workspace, list or record). {m}",
        AyaErrorKind.CreditsExhausted => "The Aya organization has used up its API credits.",
        AyaErrorKind.BadRequest => $"Aya rejected the request: {m}",
        AyaErrorKind.Server => $"Aya server error {code}. {m}",
        AyaErrorKind.Network => $"Could not reach Aya: {m}",
        _ => $"Unexpected answer from Aya: {m}",
    };
}

/// <summary>
/// A list query: <c>{"filter":…, "sorting":[…], "pagination":{limit, offset}}</c> (spec 02 §8.2).
/// <c>brickTechName</c> is the camelCase field name (<c>firma</c>, <c>meetingDate</c>), not the record key.
/// </summary>
public sealed class AyaQuery
{
    public sealed record Condition(string Field, string Value, string ValueKey = "value", string Comparator = "equal");

    public Condition? Filter { get; set; }
    /// camelCase field, or "system".
    public string? SortField { get; set; }
    public string SortValueKey { get; set; } = "value";
    public bool Descending { get; set; }
    public int Limit { get; set; } = 50;
    public int Offset { get; set; }

    public JsonObject Json()
    {
        JsonNode? filter = null;
        if (Filter is { } c)
            filter = new JsonObject
            {
                ["nodeType"] = "condition", ["brickTechName"] = c.Field, ["brickValueKey"] = c.ValueKey,
                ["comparator"] = c.Comparator, ["valueType"] = "constant", ["value"] = c.Value,
            };
        var sorting = new JsonArray();
        if (SortField is { } s)
            sorting.Add(new JsonObject { ["brickTechName"] = s, ["brickValueKey"] = SortValueKey, ["descending"] = Descending });
        return new JsonObject
        {
            ["filter"] = filter,
            ["sorting"] = sorting,
            ["pagination"] = new JsonObject { ["limit"] = Limit, ["offset"] = Offset },
        };
    }

    public AyaQuery Copy() => (AyaQuery)MemberwiseClone();
}
