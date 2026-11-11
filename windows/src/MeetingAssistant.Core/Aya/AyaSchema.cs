using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MeetingAssistant.Core.Json;

namespace MeetingAssistant.Core.Aya;

/// <summary>The kind of an Aya field (spec 02 §8.6). JSON: the lower-case name.</summary>
public enum AyaFieldKind { Text, Markdown, Select, Number, Boolean, Datetime, Date, Email, Phone, Url, Relation, Relations, Embedded, Files, Other }

/// <summary>
/// One field of an Aya record type, as far as the OpenAPI document describes it (spec 02 §8.6–8.7).
/// Aya's OpenAPI has no field labels and no select options: labels are derived from the key, options
/// are learned from existing records.
/// </summary>
public sealed class AyaField
{
    /// Record key, e.g. "firmenname__value", "firma__id".
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("kind")] public AyaFieldKind Kind { get; set; }
    [JsonPropertyName("writable")] public bool Writable { get; set; }
    /// Select only: values seen in existing records.
    [JsonPropertyName("options")] public List<string> Options { get; set; } = new();

    public AyaField() { }
    public AyaField(string key, AyaFieldKind kind, bool writable, IEnumerable<string>? options = null)
    {
        Key = key; Kind = kind; Writable = writable; Options = options?.ToList() ?? new();
    }

    /// The field name without suffix: "firmenname__value" → "firmenname".
    [JsonIgnore] public string Name => Key.Split("__")[0];
    /// "next_steps__value" → "Next steps", "maZahl" → "Ma zahl".
    [JsonIgnore] public string Label => Humanize(Name);

    /// camelCase name used by filters and sorting ("kunde_praefix" → "kundePraefix").
    [JsonIgnore]
    public string BrickTechName
    {
        get
        {
            var parts = Name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Name;
            return parts[0] + string.Concat(parts.Skip(1).Select(p => p[..1].ToUpperInvariant() + p[1..]));
        }
    }

    static readonly HashSet<AyaFieldKind> ContentKinds = new()
    {
        AyaFieldKind.Text, AyaFieldKind.Markdown, AyaFieldKind.Select, AyaFieldKind.Number, AyaFieldKind.Boolean,
        AyaFieldKind.Datetime, AyaFieldKind.Date, AyaFieldKind.Email, AyaFieldKind.Phone, AyaFieldKind.Url,
    };
    /// Plain content a person or Claude can write.
    [JsonIgnore] public bool IsContent => ContentKinds.Contains(Kind);

    /// `_` becomes a space, a space goes before each inner uppercase letter, lower-cased, first letter capitalised.
    public static string Humanize(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '_') { sb.Append(' '); continue; }
            if (char.IsUpper(ch) && i > 0) sb.Append(' ');
            sb.Append(ch);
        }
        var o = sb.ToString().ToLowerInvariant().Trim(' ');
        return o.Length == 0 ? o : o[..1].ToUpperInvariant() + o[1..];
    }

    public AyaField Clone() => new(Key, Kind, Writable, Options);
}

/// <summary>The schema of one list (record type) in one workspace (spec 02 §8.6, cached in <c>schemas.json</c>).</summary>
public sealed class AyaListSchema
{
    [JsonPropertyName("workspaceId")] public string WorkspaceId { get; set; } = "";
    [JsonPropertyName("techName")] public string TechName { get; set; } = "";
    [JsonPropertyName("fields")] public List<AyaField> Fields { get; set; } = new();
    [JsonPropertyName("fetchedAt")] public DateTime FetchedAt { get; set; }

    public AyaListSchema() { }
    public AyaListSchema(string workspaceId, string techName, IEnumerable<AyaField> fields, DateTime? fetchedAt = null)
    {
        WorkspaceId = workspaceId; TechName = techName; Fields = fields.ToList(); FetchedAt = fetchedAt ?? DateTime.UtcNow;
    }

    public AyaField? Field(string key) => Fields.FirstOrDefault(f => f.Key == key);

    /// <summary>
    /// The value that links a record to <paramref name="id"/> through the relation field <paramref name="key"/>:
    /// a list of ids for a relations field (<c>…__ids</c>), a single id otherwise. Unknown fields are judged by their key.
    /// </summary>
    public JsonNode LinkValue(string key, string id)
    {
        var many = Field(key) is { } f ? f.Kind == AyaFieldKind.Relations : key.EndsWith("__ids", StringComparison.Ordinal);
        return many ? new JsonArray(JsonValue.Create(id)) : JsonValue.Create(id)!;
    }

    [JsonIgnore] public List<AyaField> WritableContent => Fields.Where(f => f.Writable && f.IsContent).ToList();
    [JsonIgnore] public List<AyaField> Relations => Fields.Where(f => f.Kind is AyaFieldKind.Relation or AyaFieldKind.Relations).ToList();

    static readonly HashSet<string> SystemColumns = new()
        { "id", "computedTitle", "createdAt", "updatedAt", "createdBy", "modifiedBy", "createdByTitle", "modifiedByTitle", "locked" };

    /// <summary>Parses the fields of <paramref name="techName"/> from a workspace OpenAPI document (spec 02 §8.6).</summary>
    public static AyaListSchema Parse(byte[] openApi, string workspaceId, string techName)
    {
        JsonObject? paths = null, schemas = null;
        try
        {
            var doc = JsonNode.Parse(openApi);
            paths = doc?["paths"] as JsonObject;
            schemas = doc?["components"]?["schemas"] as JsonObject;
        }
        catch (JsonException) { }
        if (paths is null || schemas is null) throw new AyaException(AyaErrorKind.Decoding, "OpenAPI document could not be read");

        var path = paths.FirstOrDefault(p => p.Key.EndsWith($"/records/{techName}", StringComparison.Ordinal)).Value;
        var reference = path?["post"]?["requestBody"]?["content"]?["application/json"]?["schema"]?["$ref"].AsString();
        if (path is null || reference is null)
            throw new AyaException(AyaErrorKind.NotFound, $"List “{techName}” is not in this workspace's API documentation.");
        var createName = reference.Split('/').Last();
        var typeName = createName.Replace("CreateBody", "");
        var writable = schemas[typeName + "UpdateBody"]?["properties"]?["values"]?["properties"] as JsonObject
                       ?? schemas[createName]?["properties"]?["values"]?["properties"] as JsonObject ?? new JsonObject();
        var readable = schemas[typeName + "Record"]?["properties"] as JsonObject ?? new JsonObject();

        var fields = new List<AyaField>();
        foreach (var (key, prop) in readable)
        {
            if (SystemColumns.Contains(key)) continue;
            var w = writable.ContainsKey(key);
            if (prop?["readOnly"].AsBool() == true && !w)
            {
                // Derived columns (computed titles, colors) are not separate fields.
                if (key.Contains("__computed_title") || key.EndsWith("_color", StringComparison.Ordinal)) continue;
            }
            fields.Add(new AyaField(key, KindOf(key, w ? writable[key] : prop), w));
        }
        foreach (var (key, prop) in writable)
            if (!readable.ContainsKey(key)) fields.Add(new AyaField(key, KindOf(key, prop), true));
        fields.RemoveAll(f => f.Key.Contains("__computed_title") || f.Key.EndsWith("__record_spec_tech_name", StringComparison.Ordinal));
        return new AyaListSchema(workspaceId, techName, fields.OrderBy(f => f.Key, StringComparer.Ordinal));
    }

    /// <summary>Kind inference, first matching rule wins (spec 02 §8.6 step 6).</summary>
    public static AyaFieldKind KindOf(string key, JsonNode? prop)
    {
        var desc = (prop?["description"].AsString() ?? "").ToLowerInvariant();
        if (key.EndsWith("__ids", StringComparison.Ordinal)) return AyaFieldKind.Relations;
        if (key.EndsWith("__id", StringComparison.Ordinal)) return desc.Contains("embedded") ? AyaFieldKind.Embedded : AyaFieldKind.Relation;
        if (key.EndsWith("__file_count", StringComparison.Ordinal) || key.EndsWith("__files", StringComparison.Ordinal)) return AyaFieldKind.Files;
        if (desc.Contains("markdown")) return AyaFieldKind.Markdown;
        if (desc.Contains("option")) return AyaFieldKind.Select;
        if (desc.Contains("email")) return AyaFieldKind.Email;
        if (desc.Contains("phone")) return AyaFieldKind.Phone;
        if (desc.Contains("url") || desc.Contains("link")) return AyaFieldKind.Url;
        if (desc.Contains("datetime")) return AyaFieldKind.Datetime;
        if (desc.Contains("date")) return AyaFieldKind.Date;
        if (desc.Contains("boolean")) return AyaFieldKind.Boolean;
        if (desc.Contains("numeric") || desc.Contains("number")) return AyaFieldKind.Number;
        if (desc.Contains("text")) return AyaFieldKind.Text;
        var types = (prop?["anyOf"] is JsonArray any ? any.Select(x => x?["type"].AsString()) : new[] { prop?["type"].AsString() })
            .Where(t => t is not null).ToList();
        if (types.Contains("boolean")) return AyaFieldKind.Boolean;
        if (types.Contains("number") || types.Contains("integer")) return AyaFieldKind.Number;
        if (types.Contains("string")) return AyaFieldKind.Text;
        return AyaFieldKind.Other;
    }

    /// <summary>Fills the options of select fields with the distinct non-empty values seen in <paramref name="records"/>, sorted.</summary>
    public void LearnOptions(IEnumerable<AyaRecord> records)
    {
        var list = records.ToList();
        foreach (var f in Fields.Where(f => f.Kind == AyaFieldKind.Select))
        {
            var seen = new HashSet<string>(f.Options);
            foreach (var r in list) if (r[f.Key].AsString() is { Length: > 0 } v) seen.Add(v);
            f.Options = seen.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// Converts a value (usually text from Claude or a form) to the JSON the field expects (spec 02 §8.10).
    /// Returns false when it does not fit (do not write). On success <paramref name="result"/> null means JSON null.
    /// </summary>
    public bool TryCoerce(JsonNode? value, string key, out JsonNode? result)
    {
        result = null;
        var f = Field(key);
        if (f is null) return false;
        if (value is null || (value is JsonValue jv && jv.GetValueKind() == JsonValueKind.Null))
            return f.Kind != AyaFieldKind.Boolean;
        var text = value.Describe().Trim();
        switch (f.Kind)
        {
            case AyaFieldKind.Boolean:
                if (value.AsBool() is { } b) { result = JsonValue.Create(b); return true; }
                switch (text.ToLowerInvariant())
                {
                    case "true" or "yes" or "ja" or "1": result = JsonValue.Create(true); return true;
                    case "false" or "no" or "nein" or "0": result = JsonValue.Create(false); return true;
                    default: return false;
                }
            case AyaFieldKind.Number:
                if (value.AsNumber() is { } n) { result = JsonValue.Create(n); return true; }
                if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                { result = JsonValue.Create(d); return true; }
                return false;
            case AyaFieldKind.Select:
                if (text.Length == 0) return true;
                if (!f.Options.Contains(text)) return false;
                result = JsonValue.Create(text);
                return true;
            case AyaFieldKind.Datetime or AyaFieldKind.Date:
                if (text.Length == 0) return true;
                if (AyaText.ParseDate(text) is null && text.Length != 10) return false;
                result = JsonValue.Create(text);
                return true;
            case AyaFieldKind.Relation:
                if (text.Length > 0) result = JsonValue.Create(text);
                return true;
            case AyaFieldKind.Relations:
                if (value is JsonArray a) { result = a.DeepClone(); return true; }
                return false;
            case AyaFieldKind.Text or AyaFieldKind.Markdown or AyaFieldKind.Email or AyaFieldKind.Phone or AyaFieldKind.Url:
                result = JsonValue.Create(text);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Text convenience for <see cref="TryCoerce"/>.</summary>
    public bool TryCoerce(string text, string key, out JsonNode? result) => TryCoerce(JsonValue.Create(text), key, out result);
}

/// <summary>
/// Fields that are tracking, usage or bookkeeping data. They are hidden from Claude's context and never
/// proposed as CRM updates. Matched as parts of the lower-cased record key (spec 02 §4.1, §7.8).
/// </summary>
public static class TechnicalFields
{
    public static readonly IReadOnlyList<string> Parts = new[]
    {
        "utm_", "gclid", "ga_client", "consent", "opt_out", "do_not_contact", "external_id", "ai_enriched", "provenienz", "source",
        "marketing", "sitzungen", "letzte_anmeldung", "erstellt_am", "createdat", "updatedat", "_at__", "anz_", "aya_nutzer",
        "ist_aktiviert", "ist_intern", "nutzungsphase", "aktivitaets", "beta_", "intern_manuell", "demo_reif",
        "__record_spec", "praefix", "unbekannt", "abrechnungskonten", "organisationen", "owner",
    };

    public static bool Contains(string key)
    {
        var k = key.ToLowerInvariant();
        return Parts.Any(k.Contains);
    }
}
