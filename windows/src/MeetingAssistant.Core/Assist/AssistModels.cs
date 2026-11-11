using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Assist;

/// <summary>Status of a checklist item. JSON: missing, partial, covered.</summary>
public enum PlaybookStatus { Missing, Partial, Covered }

/// <summary>One checklist item of a template ("playbook"), e.g. P3.2 = section 3, item 2 (spec 02 §4.2).</summary>
public sealed record PlaybookItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("section")] public string Section { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("status")] public PlaybookStatus Status { get; set; } = PlaybookStatus.Missing;
    [JsonPropertyName("evidence")] public string? Evidence { get; set; }

    public PlaybookItem() { }
    public PlaybookItem(string id, string section, string text) { Id = id; Section = section; Text = text; }
}

/// <summary>Bullet and number helpers of the template parser (spec 02 §6.1).</summary>
public static class PlaybookParser
{
    /// Checklist items of a template agenda.
    public static List<PlaybookItem> Items(string markdown) => TemplateParser.Parse(markdown).Items;

    static readonly string[] Bullets = { "- [ ] ", "- [x] ", "* [ ] ", "- ", "* ", "• " };

    /// The text of a bullet or numbered line ("1. x", "1) x"), or null.
    public static string? BulletText(string line)
    {
        foreach (var p in Bullets)
            if (line.StartsWith(p, StringComparison.Ordinal)) return AssistText.TrimWs(line[p.Length..]);
        int n = 0;
        while (n < line.Length && char.IsDigit(line[n])) n++;
        if (n > 0 && line.Length > n + 1)
        {
            var rest = line[n..];
            if (rest.StartsWith(". ", StringComparison.Ordinal) || rest.StartsWith(") ", StringComparison.Ordinal))
                return AssistText.TrimWs(rest[2..]);
        }
        return null;
    }

    /// Drops a leading number such as "1." or "2.1".
    public static string StripNumber(string s)
    {
        int n = 0;
        while (n < s.Length && (char.IsDigit(s[n]) || s[n] == '.')) n++;
        return AssistText.TrimWs(s[n..]);
    }
}

/// <summary>A labelled field value: <c>{key, label, value}</c>.</summary>
public sealed record LabeledValue(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] string Value);

/// <summary>A known contact of the client: <c>{id, name, details}</c>.</summary>
public sealed class ContactInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("details")] public List<LabeledValue> Details { get; set; } = new();
}

/// <summary>An earlier meeting: <c>{title, date (yyyy-MM-dd), fields}</c>.</summary>
public sealed class RecapSummary
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("fields")] public List<LabeledValue> Fields { get; set; } = new();
}

/// <summary>
/// Everything Claude is told before the meeting starts (spec 02 §4.1). Built from Aya, stored in
/// <c>assist.json</c>. "playbook" in names means the meeting template.
/// </summary>
public sealed class AssistContext
{
    /// Null for a meeting without a client (internal, or project only).
    [JsonPropertyName("clientID")] public string? ClientID { get; set; }
    /// Empty without a client.
    [JsonPropertyName("clientTitle")] public string ClientTitle { get; set; } = "";
    [JsonPropertyName("clientFieldList")] public List<LabeledValue> ClientFieldList { get; set; } = new();
    [JsonPropertyName("contacts")] public List<ContactInfo> Contacts { get; set; } = new();
    [JsonPropertyName("projectID")] public string? ProjectID { get; set; }
    [JsonPropertyName("projectTitle")] public string? ProjectTitle { get; set; }
    [JsonPropertyName("projectFieldList")] public List<LabeledValue>? ProjectFieldList { get; set; }
    /// The planned meeting in Aya.
    [JsonPropertyName("meetingID")] public string? MeetingID { get; set; }
    [JsonPropertyName("meetingTitle")] public string? MeetingTitle { get; set; }
    /// Select value from the planned meeting or the template.
    [JsonPropertyName("meetingType")] public string? MeetingType { get; set; }
    [JsonPropertyName("playbookID")] public string? PlaybookID { get; set; }
    [JsonPropertyName("playbookTitle")] public string? PlaybookTitle { get; set; }
    [JsonPropertyName("playbookGoal")] public string? PlaybookGoal { get; set; }
    [JsonPropertyName("playbookItems")] public List<PlaybookItem> PlaybookItems { get; set; } = new();
    /// Null when the template has no tables (and in records from before tables).
    [JsonPropertyName("templateTables")] public List<TemplateTable>? TemplateTables { get; set; }
    /// Free text of the template that is neither checklist nor table question; null when none.
    [JsonPropertyName("templateNotes")] public List<string>? TemplateNotes { get; set; }
    [JsonPropertyName("recaps")] public List<RecapSummary> Recaps { get; set; } = new();
    [JsonPropertyName("goal")] public string Goal { get; set; } = "";
    [JsonPropertyName("mode")] public MeetingMode Mode { get; set; }
    [JsonPropertyName("myName")] public string MyName { get; set; } = "Me";
    [JsonPropertyName("date")] public DateTime Date { get; set; }

    /// <summary>
    /// A name for the meeting: the planned meeting's title, else client or project and template joined
    /// by " – ", else "Meeting" (spec 02 §4.1).
    /// </summary>
    [JsonIgnore]
    public string Title
    {
        get
        {
            if (!string.IsNullOrEmpty(MeetingTitle)) return MeetingTitle;
            var who = ClientID is not null ? ClientTitle : (ProjectTitle ?? "");
            var parts = new[] { who, PlaybookTitle ?? "" }.Where(p => p.Length > 0).ToList();
            return parts.Count == 0 ? "Meeting" : string.Join(" – ", parts);
        }
    }

    public static bool IsTechnical(string key) => TechnicalFields.Contains(key);

    /// <summary>
    /// German when the template reads German; without a signal there, the client and project values,
    /// earlier recaps and goal decide; still none → English (spec 02 §4.1).
    /// </summary>
    [JsonIgnore]
    public bool IsGerman
    {
        get
        {
            var template = new List<string> { PlaybookTitle ?? "", PlaybookGoal ?? "" };
            template.AddRange(PlaybookItems.Select(i => i.Section + " " + i.Text));
            template.AddRange(TemplateNotes ?? new());
            foreach (var t in TemplateTables ?? new())
            {
                template.Add(t.Prompt);
                template.AddRange(t.Columns);
                template.AddRange(t.Rows.Select(r => string.Join(" ", r.Cells)));
            }
            if (AssistLanguage.IsGerman(string.Join("\n", template)) is { } g) return g;
            var other = ClientFieldList.Select(f => f.Value)
                .Concat((ProjectFieldList ?? new()).Select(f => f.Value))
                .Concat(Recaps.SelectMany(r => r.Fields.Select(f => f.Value)))
                .Append(Goal);
            return AssistLanguage.IsGerman(string.Join("\n", other)) ?? false;
        }
    }

    /// The words a yes/no table answer becomes.
    [JsonIgnore] public (string Yes, string No) YesNoWords => IsGerman ? ("ja", "nein") : ("yes", "no");

    static readonly HashSet<string> SystemKeys = new() { "id", "computedTitle", "createdBy", "modifiedBy", "locked" };

    /// <summary>The fields of a record with content, labelled, without ids and bookkeeping (spec 02 §4.1 labeledValues).</summary>
    public static List<LabeledValue> LabeledValues(AyaRecord r, AyaListSchema? schema, ISet<string>? skip = null)
    {
        var output = new List<LabeledValue>();
        foreach (var k in r.Values.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal))
        {
            var v = r[k];
            if ((skip?.Contains(k) ?? false) || v.IsEmptyValue() || IsTechnical(k)) continue;
            var f = schema?.Field(k);
            // Relation ids mean nothing to Claude; their titles do.
            if (k.EndsWith("__id", StringComparison.Ordinal) || k.EndsWith("__ids", StringComparison.Ordinal)
                || k.EndsWith("_color", StringComparison.Ordinal) || k.EndsWith("__file_count", StringComparison.Ordinal)) continue;
            if (SystemKeys.Contains(k)) continue;
            var name = k.Split("__")[0];
            var label = k.Contains("__computed_title") ? AyaField.Humanize(name) : (f?.Label ?? AyaField.Humanize(name));
            var text = v.Describe();
            if (k is "createdAt" or "updatedAt" || f?.Kind == AyaFieldKind.Datetime) text = text.Length > 10 ? text[..10] : text;
            if (k is "createdByTitle" or "modifiedByTitle") continue;
            output.Add(new LabeledValue(k, label, text));
        }
        return output;
    }

    static readonly HashSet<string> ContactSkip = new() { "gclid__value", "ga_client_id__value", "utm_source__value", "utm_medium__value" };

    /// <summary>Builds the context from Aya records; only fields with content are kept (spec 02 §4.1).</summary>
    public static AssistContext FromAya(AyaMeetingContext aya, AyaConfig config, string goal, MeetingMode mode, string myName, DateTime date)
    {
        var c = new AssistContext
        {
            ClientID = aya.Client?.Id,
            ClientTitle = aya.Client?.Title ?? "",
            ClientFieldList = aya.Client is { } cl ? LabeledValues(cl, aya.ClientSchema) : new(),
            Contacts = aya.Contacts.Select(x => new ContactInfo
            {
                Id = x.Id, Name = x.Title, Details = LabeledValues(x, aya.ContactSchema, ContactSkip),
            }).ToList(),
            ProjectID = aya.Project?.Id,
            ProjectTitle = aya.Project?.Title,
            ProjectFieldList = aya.Project is { } p ? LabeledValues(p, aya.ProjectSchema) : null,
            MeetingID = aya.Meeting?.Id,
            MeetingTitle = aya.Meeting?.Title,
            Goal = goal, Mode = mode, MyName = myName, Date = date,
        };
        var typeFromMeeting = aya.Meeting?[config.MeetingTypeField].AsString();
        var typeFromTemplate = aya.Template?[config.TemplateTypeField].AsString();
        // A new meeting takes the template's type; a planned one keeps its own unless it has none.
        c.MeetingType = aya.Meeting is null ? (typeFromTemplate ?? typeFromMeeting) : (typeFromMeeting ?? typeFromTemplate);
        if (aya.Template is { } t)
        {
            c.PlaybookID = t.Id;
            c.PlaybookTitle = t.Title;
            c.PlaybookGoal = t[config.TemplateGoalField].AsString();
            var parsed = TemplateParser.Parse(t[config.TemplateAgendaField].AsString() ?? "");
            c.PlaybookItems = parsed.Items;
            c.TemplateTables = parsed.Tables.Count == 0 ? null : parsed.Tables;
            c.TemplateNotes = parsed.Notes.Count == 0 ? null : parsed.Notes;
        }
        var recapSkip = new HashSet<string> { config.MeetingTitleField, config.MeetingDateField, config.MeetingExternalIdField, "createdAt", "updatedAt" };
        c.Recaps = aya.Recaps.Select(r =>
        {
            var d = r[config.MeetingDateField].AsString() ?? r["createdAt"].AsString() ?? "";
            return new RecapSummary { Title = r.Title, Date = d.Length > 10 ? d[..10] : d, Fields = LabeledValues(r, aya.RecapSchema, recapSkip) };
        }).ToList();
        return c;
    }

    /// A deep copy.
    public AssistContext Clone() => JsonIO.Deserialize<AssistContext>(JsonSerializer.Serialize(this, JsonIO.Options));
}

/// <summary>A rough German/English guess from common words, for the words the app itself writes (spec 02 §4.1).</summary>
public static class AssistLanguage
{
    static readonly HashSet<string> German = new()
    {
        "der", "die", "das", "und", "ist", "nicht", "wir", "ihr", "ihre", "mit", "für", "ein", "eine", "wie", "was", "welche", "welcher",
        "haben", "nutzen", "oder", "auf", "zu", "im", "bei", "sie", "es", "von", "den", "dem", "ja", "nein", "heute", "aktuell", "kunde", "vorschlag",
    };
    static readonly HashSet<string> English = new()
    {
        "the", "and", "is", "not", "we", "you", "your", "with", "for", "a", "an", "how", "what", "which", "have", "use", "or", "on", "to",
        "in", "at", "they", "it", "of", "yes", "no", "today", "current", "customer", "proposal",
    };

    /// True for German, false for English, null when the text gives no signal. A tie counts as German.
    public static bool? IsGerman(string text)
    {
        var words = Words(text);
        var de = words.Count(German.Contains);
        var en = words.Count(English.Contains);
        if (text.Any(ch => "äöüß".Contains(ch))) de += 1;
        if (de == 0 && en == 0) return null;
        return de >= en;
    }

    /// Lower-cased words split on every non-letter.
    public static List<string> Words(string text)
    {
        var output = new List<string>();
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetter(ch)) sb.Append(ch);
            else if (sb.Length > 0) { output.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) output.Add(sb.ToString());
        return output;
    }
}

/// <summary>Status of a suggested question. JSON: suggested, asked, dismissed.</summary>
public enum QuestionStatus { Suggested, Asked, Dismissed }
/// <summary>Status of an open point. JSON: open, answered, parked.</summary>
public enum OpenPointStatus { Open, Answered, Parked }

/// <summary>A suggested question (spec 02 §4.3).</summary>
public sealed record Question
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("why")] public string? Why { get; set; }
    [JsonPropertyName("playbookItem")] public string? PlaybookItem { get; set; }
    [JsonPropertyName("status")] public QuestionStatus Status { get; set; } = QuestionStatus.Suggested;
}

/// <summary>An open point "O1" (spec 02 §4.3).</summary>
public sealed record OpenPoint
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("raisedBy")] public string? RaisedBy { get; set; }
    [JsonPropertyName("status")] public OpenPointStatus Status { get; set; } = OpenPointStatus.Open;
}

/// <summary>A person in (or mentioned in) the meeting; identity = lower-cased name (spec 02 §4.3).</summary>
public sealed record Attendee
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("company")] public string? Company { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("speaker")] public string? Speaker { get; set; }
    /// Set when the person is a known Aya contact.
    [JsonPropertyName("contactID")] public string? ContactID { get; set; }
    /// False: only mentioned or expected.
    [JsonPropertyName("present")] public bool? Present { get; set; }
    [JsonIgnore] public string Id => Name.ToLowerInvariant();
}

/// <summary>The panels of the live view (spec 02 §4.3). Stored as <c>state</c> in <c>assist.json</c>.</summary>
public sealed class AssistState
{
    /// Current suggestions, best first.
    [JsonPropertyName("questions")] public List<Question> Questions { get; set; } = new();
    [JsonPropertyName("askedQuestions")] public List<Question> AskedQuestions { get; set; } = new();
    /// One paragraph per topic, each starting "Topic: …".
    [JsonPropertyName("summary")] public List<string> Summary { get; set; } = new();
    [JsonPropertyName("openPoints")] public List<OpenPoint> OpenPoints { get; set; } = new();
    [JsonPropertyName("attendees")] public List<Attendee> Attendees { get; set; } = new();
    [JsonPropertyName("playbook")] public List<PlaybookItem> Playbook { get; set; } = new();
    /// The template's fill-in tables with what was answered so far (null before tables existed).
    [JsonPropertyName("tables")] public List<TemplateTable>? Tables { get; set; }
    /// Live speaker label ("system-1") → the name shown and sent to Claude.
    [JsonPropertyName("speakerNames")] public Dictionary<string, string>? SpeakerNames { get; set; }
    /// Successful turns.
    [JsonPropertyName("updates")] public int Updates { get; set; }
    /// API-equivalent cost reported by the CLI, summed across restarts.
    [JsonPropertyName("costUSD")] public double CostUSD { get; set; }
    /// Ids touched by the latest update (for highlighting). Insertion-ordered set.
    [JsonPropertyName("changed")] public HashSet<string> Changed { get; set; } = new();

    /// A deep copy (published to the UI).
    public AssistState Clone() => JsonIO.Deserialize<AssistState>(JsonSerializer.Serialize(this, JsonIO.Options));

    /// <summary>Applies one op (spec 02 §3.6). Returns the ids it changed.</summary>
    public List<string> Apply(AssistOp op)
    {
        switch (op)
        {
            case AssistOp.NextQuestions nq:
                {
                    // Questions are numbered per update by Claude; give them unique ids.
                    var stamp = Updates;
                    Questions = nq.Items.Select((q, i) => q with { Id = $"u{stamp}-{i}" }).ToList();
                    return Questions.Select(q => q.Id).Append("questions").ToList();
                }
            case AssistOp.SummaryAppend sa:
                {
                    var start = Summary.Count;
                    Summary.AddRange(sa.Paragraphs);
                    return Enumerable.Range(start, Summary.Count - start).Select(i => $"s{i}").ToList();
                }
            case AssistOp.SummaryReplace sr:
                if (sr.Index < 0 || sr.Index >= Summary.Count) { Summary.Add(sr.Text); return new() { $"s{Summary.Count - 1}" }; }
                Summary[sr.Index] = sr.Text;
                return new() { $"s{sr.Index}" };
            case AssistOp.SetOpenPoint op2:
                {
                    var p = op2.Point;
                    var i = OpenPoints.FindIndex(x => x.Id == p.Id);
                    if (i >= 0)
                    {
                        var n = p with { };
                        n.RaisedBy ??= OpenPoints[i].RaisedBy;
                        OpenPoints[i] = n;
                    }
                    else OpenPoints.Add(p with { });
                    return new() { $"o:{p.Id}" };
                }
            case AssistOp.SetAttendee sa2:
                {
                    var a = sa2.Attendee;
                    var i = Attendees.FindIndex(x => x.Id == a.Id
                        || (x.Speaker is not null && x.Speaker == a.Speaker && a.Speaker is not null && x.ContactID is null));
                    if (i >= 0)
                    {
                        var m = Attendees[i] with { };
                        m.Name = a.Name;
                        m.Role = a.Role ?? m.Role;
                        m.Company = a.Company ?? m.Company;
                        m.Notes = a.Notes ?? m.Notes;
                        m.Speaker = a.Speaker ?? m.Speaker;
                        m.ContactID = a.ContactID ?? m.ContactID;
                        m.Present = a.Present ?? m.Present ?? (a.Speaker is not null ? true : null);
                        Attendees[i] = m;
                    }
                    else Attendees.Add(a with { });
                    return new() { $"a:{a.Id}" };
                }
            case AssistOp.SetPlaybook pb:
                {
                    var i = Playbook.FindIndex(x => x.Id == pb.Item);
                    if (i < 0) return new();
                    Playbook[i].Status = pb.Status;
                    if (pb.Evidence is not null) Playbook[i].Evidence = pb.Evidence;
                    return new() { $"p:{pb.Item}" };
                }
            case AssistOp.TableCell tc:
                {
                    var tid = tc.Row.Split('.')[0];
                    var t = Tables?.FirstOrDefault(x => x.Id == tid);
                    if (t is null || !t.Set(tc.Row, tc.Column, tc.Value, tc.Evidence)) return new();
                    return new() { $"t:{tc.Row}" };
                }
        }
        return new();
    }
}

/// <summary>One line of Claude's output: one JSON object that changes one panel (spec 02 §3.6).</summary>
public abstract record AssistOp
{
    public sealed record NextQuestions(List<Question> Items) : AssistOp
    {
        public bool Equals(NextQuestions? o) => o is not null && Items.SequenceEqual(o.Items);
        public override int GetHashCode() => Items.Count;
    }
    public sealed record SummaryAppend(List<string> Paragraphs) : AssistOp
    {
        public bool Equals(SummaryAppend? o) => o is not null && Paragraphs.SequenceEqual(o.Paragraphs);
        public override int GetHashCode() => Paragraphs.Count;
    }
    public sealed record SummaryReplace(int Index, string Text) : AssistOp;
    public sealed record SetOpenPoint(OpenPoint Point) : AssistOp;
    public sealed record SetAttendee(Attendee Attendee) : AssistOp;
    public sealed record SetPlaybook(string Item, PlaybookStatus Status, string? Evidence) : AssistOp;
    public sealed record TableCell(string Row, string Column, string Value, string? Evidence) : AssistOp;

    static readonly Dictionary<string, PlaybookStatus> PlaybookStatuses =
        new() { ["missing"] = PlaybookStatus.Missing, ["partial"] = PlaybookStatus.Partial, ["covered"] = PlaybookStatus.Covered };
    static readonly Dictionary<string, OpenPointStatus> OpenStatuses =
        new() { ["open"] = OpenPointStatus.Open, ["answered"] = OpenPointStatus.Answered, ["parked"] = OpenPointStatus.Parked };

    /// <summary>
    /// Parses one output line; null for anything that is not a valid op (prose, fences, partial JSON).
    /// A boolean table value becomes the meeting's yes/no word (spec 02 §3.6).
    /// </summary>
    public static AssistOp? Parse(string line, (string Yes, string No)? yesNo = null)
    {
        var yn = yesNo ?? ("yes", "no");
        var s = AssistText.TrimWs(line);
        if (s.StartsWith("```", StringComparison.Ordinal) || s.Length == 0) return null;
        if (s.EndsWith(',')) s = s[..^1];
        if (!s.StartsWith('{')) return null;
        JsonObject? v;
        try { v = JsonNode.Parse(s) as JsonObject; } catch (JsonException) { return null; }
        if (v is null || v["op"].AsString() is not { } op) return null;

        static string? Str(JsonNode? o, string k)
        {
            var t = o?[k].AsString();
            if (t is null) return null;
            t = AssistText.TrimAll(t);
            return t.Length == 0 ? null : t;
        }

        switch (op)
        {
            case "next_questions":
                {
                    var items = new List<Question>();
                    if (v["items"] is JsonArray arr)
                        for (int i = 0; i < arr.Count; i++)
                        {
                            if (Str(arr[i], "text") is not { } text) continue;
                            items.Add(new Question
                            {
                                Id = Str(arr[i], "id") ?? $"Q{i + 1}", Text = text, Why = Str(arr[i], "why"), PlaybookItem = Str(arr[i], "playbook_item"),
                            });
                        }
                    return items.Count == 0 ? null : new NextQuestions(items);
                }
            case "summary_append":
                {
                    var b = (v["bullets"] as JsonArray ?? new JsonArray()).Select(x => x.AsString()).Where(x => x is not null)
                        .Select(x => AssistText.TrimAll(x!)).Where(x => x.Length > 0).ToList();
                    return b.Count == 0 ? null : new SummaryAppend(b);
                }
            case "summary_replace":
                if (v["index"].AsNumber() is not { } idx || Str(v, "text") is not { } t) return null;
                return new SummaryReplace((int)idx, t);
            case "open_point":
                {
                    if (Str(v, "id") is not { } id || Str(v, "text") is not { } text) return null;
                    var st = OpenStatuses.TryGetValue(Str(v, "status") ?? "open", out var x) ? x : OpenPointStatus.Open;
                    return new SetOpenPoint(new OpenPoint { Id = id, Text = text, RaisedBy = Str(v, "raised_by"), Status = st });
                }
            case "attendee":
                if (Str(v, "name") is not { } name) return null;
                return new SetAttendee(new Attendee
                {
                    Name = name, Role = Str(v, "role"), Company = Str(v, "company"), Notes = Str(v, "notes"),
                    Speaker = Str(v, "speaker"), ContactID = Str(v, "contact_id"), Present = v["present"].AsBool(),
                });
            case "playbook":
                {
                    if (Str(v, "item") is not { } item || !PlaybookStatuses.TryGetValue(Str(v, "status") ?? "", out var st)) return null;
                    return new SetPlaybook(item, st, Str(v, "evidence"));
                }
            case "table_cell":
                {
                    string? value = null;
                    if (v.TryGetPropertyValue("value", out var j))
                    {
                        if (j.AsBool() is { } b) value = b ? yn.Yes : yn.No;
                        else value = j.AsString() ?? (j is null || (j is JsonValue jv && jv.GetValueKind() == JsonValueKind.Null) ? "" : j.Describe());
                        value = AssistText.TrimAll(value);
                    }
                    if (Str(v, "row") is not { } row || Str(v, "column") is not { } column || string.IsNullOrEmpty(value)) return null;
                    return new TableCell(row, column, value, Str(v, "evidence"));
                }
        }
        return null;
    }
}
