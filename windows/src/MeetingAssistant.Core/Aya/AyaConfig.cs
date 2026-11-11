using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Platform;

namespace MeetingAssistant.Core.Aya;

/// <summary>The Aya lists MeetingAssistant works with (spec 02 §8.4). JSON / dictionary key: the lower-case name.</summary>
public enum AyaSlot { Clients, Contacts, Projects, Templates, Meetings, Members }

public static class AyaSlots
{
    public static readonly IReadOnlyList<AyaSlot> All = Enum.GetValues<AyaSlot>();

    public static string Raw(this AyaSlot s) => s.ToString().ToLowerInvariant();
    public static AyaSlot? Parse(string raw) => All.Cast<AyaSlot?>().FirstOrDefault(s => s!.Value.Raw() == raw);

    public static string Title(this AyaSlot s) => s switch
    {
        AyaSlot.Clients => "Clients",
        AyaSlot.Contacts => "Contacts",
        AyaSlot.Projects => "Projects",
        AyaSlot.Templates => "Meeting templates",
        AyaSlot.Meetings => "Meetings",
        _ => "Team members",
    };

    public static string Explanation(this AyaSlot s) => s switch
    {
        AyaSlot.Clients => "Companies you meet with (your CRM).",
        AyaSlot.Contacts => "People at those companies; used to recognise attendees.",
        AyaSlot.Projects => "Projects a meeting can belong to.",
        AyaSlot.Templates => "Agenda and questions; they become the meeting's checklist.",
        AyaSlot.Meetings => "Planned meetings, and where the recap is saved.",
        _ => "Your team; internal attendees are linked as participants.",
    };

    /// Default list (techName), used when connecting for the first time.
    public static string DefaultTechName(this AyaSlot s) => s switch
    {
        AyaSlot.Clients => "firma",
        AyaSlot.Contacts => "kontakt",
        AyaSlot.Projects => "pmProjects",
        AyaSlot.Templates => "pmMeetingTemplates",
        AyaSlot.Meetings => "pmMeetings",
        _ => "members",
    };

    /// Lists Assist cannot work without. Projects and members are used when they exist.
    public static bool IsRequired(this AyaSlot s) => s is AyaSlot.Clients or AyaSlot.Contacts or AyaSlot.Templates or AyaSlot.Meetings;
}

/// <summary>A chosen list: its (home) workspace and record type (spec 02 §8.4).</summary>
public sealed record AyaListRef(
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("workspaceLabel")] string WorkspaceLabel,
    [property: JsonPropertyName("techName")] string TechName,
    [property: JsonPropertyName("label")] string Label);

/// <summary>
/// Settings for the Aya connection (spec 02 §8.4), persisted under the preferences key <c>aya.config.v2</c>.
/// The API key is kept in the secure store, not here. Field names are record keys. The five link fields
/// tell a missing key (default) from an explicit null (not linked) and are always written.
/// </summary>
[JsonConverter(typeof(AyaConfigConverter))]
public sealed record AyaConfig
{
    public string OrgId { get; set; } = "";
    /// Slot raw name → list.
    public Dictionary<string, AyaListRef> Lists { get; set; } = new();

    /// Contact → client link.
    public string? ContactsClientField { get; set; } = "firma__id";
    public string? MeetingClientField { get; set; } = "customer__id";
    public string? MeetingProjectField { get; set; } = "project__id";
    public string? MeetingTemplateField { get; set; } = "load_template__id";
    public string? MeetingParticipantsField { get; set; } = "participants__ids";

    public string MeetingTitleField { get; set; } = "title__value";
    public string MeetingDateField { get; set; } = "meeting_date__value";
    public string MeetingTypeField { get; set; } = "meeting_type__value";
    public string MeetingExternalIdField { get; set; } = "external_meeting_id__value";
    /// Where filled template tables are saved: null = automatic, "-" = not saved, else a field key.
    public string? MeetingTablesField { get; set; }

    public string TemplateAgendaField { get; set; } = "agenda__value";
    public string TemplateGoalField { get; set; } = "description__value";
    public string TemplateTypeField { get; set; } = "meeting_type__value";

    /// A deep copy (the lists dictionary is not shared).
    public AyaConfig Copy() => this with { Lists = new Dictionary<string, AyaListRef>(Lists) };

    public AyaListRef? List(AyaSlot slot) => Lists.TryGetValue(slot.Raw(), out var r) ? r : null;
    public void SetList(AyaSlot slot, AyaListRef? r)
    {
        if (r is null) Lists.Remove(slot.Raw()); else Lists[slot.Raw()] = r;
    }

    /// The org id is set and every required slot has a list.
    public bool IsComplete => OrgId.Length > 0 && AyaSlots.All.Where(s => s.IsRequired()).All(s => List(s) is not null);

    /// <summary>The meeting field that receives filled template tables, or null when they are not saved (spec 02 §8.4).</summary>
    public string? TablesField(AyaListSchema schema)
    {
        if (MeetingTablesField == "-") return null;
        var markdown = schema.WritableContent.Where(f => f.Kind == AyaFieldKind.Markdown).ToList();
        if (MeetingTablesField is { } f) return schema.Field(f) is not null ? f : null;
        var names = new[] { "protokoll", "leitfaden", "notes", "notiz", "mitschrift", "minutes", "template", "vorlage" };
        var hit = markdown.FirstOrDefault(x => names.Any(n => x.Key.ToLowerInvariant().Contains(n) || x.Label.ToLowerInvariant().Contains(n)));
        if (hit is not null) return hit.Key;
        return markdown.FirstOrDefault(x => x.Key.StartsWith("summary", StringComparison.Ordinal))?.Key ?? markdown.FirstOrDefault()?.Key;
    }

    public bool Equals(AyaConfig? other) =>
        other is not null && ToJson() == other.ToJson();
    public override int GetHashCode() => ToJson().GetHashCode();

    // MARK: Persistence

    /// Preferences key of the config.
    public const string PreferencesKey = "aya.config.v2";
    /// Before meeting templates: playbooks and a separate recap list.
    public const string LegacyPreferencesKey = "aya.config.v1";

    public string ToJson() => JsonIO.Compact(JsonSerializer.SerializeToNode(this, JsonIO.Options));
    public static AyaConfig FromJson(string json) => JsonSerializer.Deserialize<AyaConfig>(json, JsonIO.Options) ?? new AyaConfig();

    /// <summary>Loads the config from the preferences, migrating <c>aya.config.v1</c> when needed.</summary>
    public static AyaConfig Load(IPreferenceStore? prefs = null)
    {
        prefs ??= FilePreferences.Shared;
        if (prefs.GetString(PreferencesKey) is { } s)
        {
            try { return FromJson(s); } catch (JsonException) { }
        }
        if (prefs.GetString(LegacyPreferencesKey) is { } old) return Migrate(old);
        return new AyaConfig();
    }

    public void Save(IPreferenceStore? prefs = null) => (prefs ?? FilePreferences.Shared).SetString(PreferencesKey, ToJson());

    /// <summary>
    /// Keeps the connection, the client and contact lists and the contact link; the meeting lists are
    /// picked again when the app next talks to Aya (spec 02 §8.4 legacy migration).
    /// </summary>
    public static AyaConfig Migrate(string legacyJson)
    {
        var c = new AyaConfig();
        JsonObject? old;
        try { old = JsonNode.Parse(legacyJson) as JsonObject; } catch (JsonException) { return c; }
        if (old is null) return c;
        c.OrgId = old["orgId"].AsString() ?? "";
        foreach (var slot in new[] { AyaSlot.Clients, AyaSlot.Contacts })
        {
            if (old["lists"]?[slot.Raw()] is JsonObject r)
            {
                try { c.SetList(slot, r.Deserialize<AyaListRef>(JsonIO.Options)); } catch (JsonException) { }
            }
        }
        if (old["contactsClientField"].AsString() is { } link) c.ContactsClientField = link;
        return c;
    }

    // MARK: API key

    /// Account name of the API key in the secure store.
    public const string ApiKeyAccount = "aya-api-key";

    /// <summary>The API key: <c>AYA_API_KEY</c> when set, else the secure store (spec 02 §8.1).</summary>
    public static string? ApiKey(ISecretStore? store)
    {
        var env = Environment.GetEnvironmentVariable("AYA_API_KEY");
        if (!string.IsNullOrEmpty(env)) return env;
        var v = store?.Load(ApiKeyAccount);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>Stores the key, or deletes it for null/empty.</summary>
    public static void SetApiKey(ISecretStore store, string? key) => store.Save(ApiKeyAccount, string.IsNullOrEmpty(key) ? null : key);

    /// <summary>A client for these settings, or null when the org id or key is missing.</summary>
    public AyaClient? Client(ISecretStore? store, HttpMessageHandler? handler = null)
    {
        if (OrgId.Length == 0 || ApiKey(store) is not { Length: > 0 } key) return null;
        return new AyaClient(OrgId, key, handler: handler);
    }

    // MARK: Web URLs (spec 02 §8.4)

    /// The organization in the Aya web app.
    public string? OrgUrl
    {
        get
        {
            var id = OrgId.Trim();
            return id.Length == 0 ? null : "https://app.hey-aya.ai/org/" + Uri.EscapeDataString(id);
        }
    }

    /// A list in the Aya web app, or one record in it.
    public string? WebUrl(AyaSlot slot, string? recordId = null)
    {
        if (List(slot) is not { } r) return null;
        var s = $"https://app.hey-aya.ai/workspace/{Uri.EscapeDataString(r.WorkspaceId)}/recordlist/{Uri.EscapeDataString(r.TechName)}";
        if (!string.IsNullOrEmpty(recordId)) s += "/record/" + Uri.EscapeDataString(recordId);
        return s;
    }

    /// Where the logo leads: the meetings list, else the organization.
    public string? LogoUrl => WebUrl(AyaSlot.Meetings) ?? OrgUrl;
}

/// Reads and writes <see cref="AyaConfig"/> with the Mac's null-vs-missing rules.
public sealed class AyaConfigConverter : JsonConverter<AyaConfig>
{
    static readonly string[] Links = { "contactsClientField", "meetingClientField", "meetingProjectField", "meetingTemplateField", "meetingParticipantsField" };

    public override AyaConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("config object expected");
        var c = new AyaConfig();
        string Str(string k, string d) => o[k].AsString() ?? d;
        // A missing key means "default"; an explicit null means "not linked".
        string? Link(string k, string? d) => o.ContainsKey(k) ? o[k].AsString() : d;
        c.OrgId = Str("orgId", c.OrgId);
        if (o["lists"] is JsonObject lists)
        {
            try { c.Lists = lists.Deserialize<Dictionary<string, AyaListRef>>(options) ?? new(); } catch (JsonException) { }
        }
        c.ContactsClientField = Link("contactsClientField", c.ContactsClientField);
        c.MeetingClientField = Link("meetingClientField", c.MeetingClientField);
        c.MeetingProjectField = Link("meetingProjectField", c.MeetingProjectField);
        c.MeetingTemplateField = Link("meetingTemplateField", c.MeetingTemplateField);
        c.MeetingParticipantsField = Link("meetingParticipantsField", c.MeetingParticipantsField);
        c.MeetingTitleField = Str("meetingTitleField", c.MeetingTitleField);
        c.MeetingDateField = Str("meetingDateField", c.MeetingDateField);
        c.MeetingTypeField = Str("meetingTypeField", c.MeetingTypeField);
        c.MeetingExternalIdField = Str("meetingExternalIdField", c.MeetingExternalIdField);
        c.MeetingTablesField = o["meetingTablesField"].AsString();
        c.TemplateAgendaField = Str("templateAgendaField", c.TemplateAgendaField);
        c.TemplateGoalField = Str("templateGoalField", c.TemplateGoalField);
        c.TemplateTypeField = Str("templateTypeField", c.TemplateTypeField);
        return c;
    }

    public override void Write(Utf8JsonWriter w, AyaConfig c, JsonSerializerOptions options)
    {
        w.WriteStartObject();
        w.WriteString("orgId", c.OrgId);
        w.WritePropertyName("lists");
        JsonSerializer.Serialize(w, c.Lists, options);
        void S(string k, string? v) { if (v is null) w.WriteNull(k); else w.WriteString(k, v); }
        S("contactsClientField", c.ContactsClientField);       // explicit null = no link
        S("meetingClientField", c.MeetingClientField);
        S("meetingProjectField", c.MeetingProjectField);
        S("meetingTemplateField", c.MeetingTemplateField);
        S("meetingParticipantsField", c.MeetingParticipantsField);
        S("meetingTitleField", c.MeetingTitleField);
        S("meetingDateField", c.MeetingDateField);
        S("meetingTypeField", c.MeetingTypeField);
        S("meetingExternalIdField", c.MeetingExternalIdField);
        S("meetingTablesField", c.MeetingTablesField);
        S("templateAgendaField", c.TemplateAgendaField);
        S("templateGoalField", c.TemplateGoalField);
        S("templateTypeField", c.TemplateTypeField);
        w.WriteEndObject();
    }
}

/// <summary>String preferences by key (the Mac's user defaults domain <c>de.local.MeetingScribe</c>).</summary>
public interface IPreferenceStore
{
    string? GetString(string key);
    void SetString(string key, string? value);
}

/// <summary>
/// String preferences backed by the app's single preferences file through
/// <see cref="MeetingAssistant.Core.Storage.SettingsStore"/>, so all parts of the app share one
/// in-memory copy and never overwrite each other's keys. Values written here are strings (the Mac
/// stores <c>aya.config.v2</c> and <c>assist.settings.v1</c> as JSON strings).
/// </summary>
public sealed class FilePreferences : IPreferenceStore
{
    readonly Func<MeetingAssistant.Core.Storage.SettingsStore> _store;

    public FilePreferences(Func<string>? path = null)
    {
        if (path is null) _store = () => MeetingAssistant.Core.Storage.SettingsStore.Shared;
        else
        {
            MeetingAssistant.Core.Storage.SettingsStore? own = null;
            string? ownPath = null;
            _store = () =>
            {
                var p = path();
                if (own is null || ownPath != p) { own = new MeetingAssistant.Core.Storage.SettingsStore(p); ownPath = p; }
                return own;
            };
        }
    }

    /// The app's preferences file.
    public static FilePreferences Shared { get; } = new();

    public string? GetString(string key)
    {
        var v = _store().Get(key);
        return v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String ? jv.GetValue<string>() : v is null ? null : JsonIO.Compact(v);
    }

    public void SetString(string key, string? value) => _store().SetString(key, value);
}

/// <summary>Preferences in memory (tests, msctl dry runs).</summary>
public sealed class MemoryPreferences : IPreferenceStore
{
    readonly Dictionary<string, string> _values = new();
    public string? GetString(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public void SetString(string key, string? value) { if (value is null) _values.Remove(key); else _values[key] = value; }
}
