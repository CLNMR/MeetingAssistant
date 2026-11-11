using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Claude;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Assist;

/// <summary>
/// What Assist keeps with a recording (spec 02 §7.9): the context, the final panels, the transcript Claude
/// saw, and what was written to Aya. Stored as <c>assist.json</c> in the session folder.
/// </summary>
public sealed class AssistRecord
{
    [JsonPropertyName("context")] public AssistContext Context { get; set; } = new();
    [JsonPropertyName("state")] public AssistState State { get; set; } = new();
    /// Everything Claude was sent (history + pending).
    [JsonPropertyName("lines")] public List<AssistLine> Lines { get; set; } = new();
    /// The Aya meeting the recap was written to.
    [JsonPropertyName("recapRecordID")] public string? RecapRecordID { get; set; }
    [JsonPropertyName("recapUploadedAt")] public DateTime? RecapUploadedAt { get; set; }
    [JsonPropertyName("crmAppliedAt")] public DateTime? CrmAppliedAt { get; set; }
    /// Always written (may be empty).
    [JsonPropertyName("createdContactIDs")] public List<string> CreatedContactIDs { get; set; } = new();
    /// Tables after the post-meeting completion and user edits; null = not completed yet.
    [JsonPropertyName("tables")] public List<TemplateTable>? Tables { get; set; }
    /// The recap as last drafted or edited.
    [JsonPropertyName("recapDraft")] public List<ProposedValue>? RecapDraft { get; set; }
    /// Live speaker names were carried over to the final transcript once.
    [JsonPropertyName("speakersCarried")] public bool? SpeakersCarried { get; set; }

    public AssistRecord() { }
    public AssistRecord(AssistContext context, AssistState state, IEnumerable<AssistLine> lines)
    {
        Context = context; State = state; Lines = lines.ToList();
    }

    public static string PathFor(string sessionID) => AppPaths.AssistFile(sessionID);
    /// The record of a session, or null.
    public static AssistRecord? Load(string sessionID) => JsonIO.TryRead<AssistRecord>(PathFor(sessionID), out var r) ? r : null;
    public void Save(string sessionID) => JsonIO.Write(this, PathFor(sessionID));
    public AssistRecord Clone() => JsonIO.Deserialize<AssistRecord>(JsonSerializer.Serialize(this, JsonIO.Options));
}

/// <summary>A value proposed for one Aya field, editable in the review (spec 02 §7.4). Its id is the key.</summary>
public sealed record ProposedValue
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("kind")] public AyaFieldKind Kind { get; set; }
    [JsonPropertyName("options")] public List<string> Options { get; set; } = new();
    [JsonPropertyName("old")] public string Old { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("include")] public bool Include { get; set; }
    [JsonIgnore] public string Id => Key;

    public bool Equals(ProposedValue? o) =>
        o is not null && Key == o.Key && Label == o.Label && Kind == o.Kind && Options.SequenceEqual(o.Options) && Old == o.Old
        && Value == o.Value && Reason == o.Reason && Include == o.Include;
    public override int GetHashCode() => HashCode.Combine(Key, Value, Include);
    public ProposedValue Copy() => this with { Options = Options.ToList() };
}

/// <summary>Proposed changes of one contact (an existing one, or a new one when <see cref="ContactID"/> is null).</summary>
public sealed class ContactProposal
{
    public string? ContactID { get; set; }
    public string Name { get; set; } = "";
    public List<ProposedValue> Changes { get; set; } = new();
    public bool Include { get; set; }
    public string Id => ContactID ?? "new-" + Name;
}

/// <summary>A CRM update proposal (spec 02 §7.8).</summary>
public sealed class CRMProposal
{
    public List<ProposedValue> Client { get; set; } = new();
    public List<ContactProposal> Contacts { get; set; } = new();
    public bool IsEmpty => Client.Count == 0 && Contacts.Count == 0;
}

/// <summary>The post-meeting steps: recap draft, values for Aya, tables, CRM proposals (spec 02 §7).</summary>
public static class RecapBuilder
{
    /// <summary>The transcript as text lines: the final transcript's turns when it has any, else the live lines (spec 02 §7.2).</summary>
    public static List<string> TranscriptLines(string sessionID, IEnumerable<AssistLine> fallback)
    {
        var path = AppPaths.TranscriptFile(sessionID);
        if (JsonIO.TryRead<Transcript>(path, out var t) && t is { Turns.Count: > 0 })
            return t.Turns.Select(turn => new AssistLine(turn.Start, t.Label(turn.Speaker), turn.Text).Rendered).ToList();
        return fallback.Select(l => l.Rendered).ToList();
    }

    // MARK: Recap

    /// <summary>Fields Claude fills in: writable content fields except those the app sets itself (spec 02 §7.4).</summary>
    public static List<AyaField> RecapFields(AyaListSchema schema, AyaConfig config)
    {
        var appSet = new HashSet<string> { config.MeetingDateField, config.MeetingExternalIdField, config.MeetingTypeField };
        // A dedicated field for the template's tables is filled by the app, not drafted.
        if (config.TablesField(schema) is { } t && !t.StartsWith("summary", StringComparison.Ordinal)) appSet.Add(t);
        return schema.WritableContent.Where(f => !appSet.Contains(f.Key) && f.Kind != AyaFieldKind.Datetime && f.Kind != AyaFieldKind.Date
                                                 && !(f.Kind == AyaFieldKind.Select && f.Options.Count == 0)).ToList();
    }

    static JsonArray Strings(IEnumerable<string> xs) => new(xs.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    /// <summary>The JSON schema of one recap field (spec 02 §7.4 table).</summary>
    public static JsonObject PropertySchema(AyaField f)
    {
        var desc = f.Label + (f.Kind == AyaFieldKind.Markdown ? " (markdown)" : "");
        return f.Kind switch
        {
            AyaFieldKind.Number => new JsonObject { ["type"] = Strings(new[] { "number", "null" }), ["description"] = desc },
            AyaFieldKind.Boolean => new JsonObject { ["type"] = Strings(new[] { "boolean", "null" }), ["description"] = desc },
            AyaFieldKind.Select => new JsonObject
            {
                ["enum"] = new JsonArray(f.Options.Select(o => (JsonNode?)JsonValue.Create(o)).Append(null).ToArray()),
                ["description"] = desc,
            },
            _ => new JsonObject { ["type"] = Strings(new[] { "string", "null" }), ["description"] = desc },
        };
    }

    /// <summary>An object schema with every field required and no other properties.</summary>
    public static JsonObject ObjectSchema(IEnumerable<AyaField> fields)
    {
        var list = fields.ToList();
        var props = new JsonObject();
        foreach (var f in list) props[f.Key] = PropertySchema(f);
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = props, ["required"] = Strings(list.Select(f => f.Key)), ["additionalProperties"] = false,
        };
    }

    /// <summary>The recap prompt (spec 02 §7.4).</summary>
    public static string RecapPrompt(AssistRecord record, IReadOnlyList<string> transcript, IReadOnlyList<AyaField> fields)
    {
        var p = ContextText(record) + "\n\nTRANSCRIPT:\n" + string.Join("\n", transcript);
        p += "\n\nFill in the meeting recap. Fields:\n";
        foreach (var f in fields)
            p += $"- {f.Key}: {f.Label}{(f.Kind == AyaFieldKind.Markdown ? " — markdown" : "")}{(f.Kind == AyaFieldKind.Select ? " — one of: " + string.Join(", ", f.Options) : "")}\n";
        p += "The title should name the client or project and the topic. Attendee fields: names and roles of the external participants.";
        if (record.Context.PlaybookTitle is { } t && record.Context.PlaybookItems.Count > 0)
            p += $" The meeting followed the template \"{t}\"; where it fits, structure the summary along its agenda.";
        return p;
    }

    /// <summary>
    /// Asks Claude for the recap fields (spec 02 §7.4). With <paramref name="existing"/> (the Aya meeting the
    /// recap goes to), its current values are shown and fields with content are not ticked.
    /// </summary>
    public static async Task<List<ProposedValue>> DraftRecapAsync(string binary, string model, AssistRecord record, string sessionID,
                                                                  AyaListSchema schema, AyaConfig config, AyaRecord? existing = null,
                                                                  CancellationToken ct = default)
    {
        var fields = RecapFields(schema, config);
        if (fields.Count == 0) return new();
        var transcript = TranscriptLines(sessionID, record.Lines);
        var r = await ClaudeCli.OneShotAsync(binary, AssistPrompts.RecapSystem, RecapPrompt(record, transcript, fields), model, "medium",
                                             ObjectSchema(fields), ct: ct);
        return ReadRecap(r.Structured, fields, string.Join("\n", transcript), existing);
    }

    /// <summary>Turns Claude's structured recap into proposed values, with the deadline check (spec 02 §7.4 "Result").</summary>
    public static List<ProposedValue> ReadRecap(JsonNode? structured, IReadOnlyList<AyaField> fields, string spoken, AyaRecord? existing)
    {
        if (structured is not JsonObject output) throw ClaudeException.Failed("no structured recap returned");
        var drafted = fields.Select(f =>
        {
            var v = output[f.Key];
            var isNull = v is null || (v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Null);
            var value = isNull ? "" : v.Describe();
            string? note = null;
            // Deadlines nobody said are taken out again, and the user is told.
            if (f.Kind is AyaFieldKind.Markdown or AyaFieldKind.Text)
            {
                var checkedText = DeadlineCheck.Filter(value, spoken);
                if (checkedText.Removed.Count > 0)
                {
                    value = checkedText.Text;
                    note = "Removed deadlines not said in the meeting: " + string.Join(", ", checkedText.Removed.Select(d => $"“{d}”"));
                }
            }
            return new ProposedValue
            {
                Key = f.Key, Label = f.Label, Kind = f.Kind, Options = f.Options.ToList(), Old = "", Value = value, Reason = note,
                Include = !v.IsEmptyValue(),
            };
        }).ToList();
        return Target(drafted, existing);
    }

    /// <summary>
    /// Shows the target meeting's current values and decides what is written by default: a field is ticked
    /// when the draft has something and the meeting has nothing there yet (spec 02 §7.4 target).
    /// </summary>
    public static List<ProposedValue> Target(IEnumerable<ProposedValue> fields, AyaRecord? existing) =>
        fields.Select(p =>
        {
            var q = p.Copy();
            var cur = existing?[p.Key];
            q.Old = existing is null || !existing.Has(p.Key) ? "" : cur.Describe();
            q.Include = AssistText.TrimAll(q.Value).Length > 0 && q.Old.Length == 0;
            return q;
        }).ToList();

    /// <summary>
    /// Record values for saving the recap (spec 02 §7.5): the reviewed fields plus what the app fills in.
    /// For an existing meeting, links, date and type are only filled where it has none yet, and team
    /// members are added to its participants. A null value means "write JSON null".
    /// </summary>
    public static Dictionary<string, JsonNode?> RecapValues(IEnumerable<ProposedValue> fields, AssistRecord record, string sessionID,
                                                            DateTime startedAt, AyaListSchema schema, AyaConfig config,
                                                            AyaRecord? existing = null, IReadOnlyList<string>? memberIDs = null,
                                                            IReadOnlyList<TemplateTable>? tables = null)
    {
        memberIDs ??= Array.Empty<string>();
        var values = new Dictionary<string, JsonNode?>();
        foreach (var p in fields.Where(p => p.Include))
            if (schema.TryCoerce(p.Value, p.Key, out var v)) values[p.Key] = v;
        // Filled tables go under a heading named after the template, replacing that block if it is there.
        if (config.TablesField(schema) is { } key
            && TemplateParser.Block(record.Context.PlaybookTitle ?? "Template", tables ?? Array.Empty<TemplateTable>()) is { } block)
        {
            var b = (values.TryGetValue(key, out var cur) ? cur.AsString() : null) ?? existing?[key].AsString() ?? "";
            values[key] = TemplateParser.Merge(b, block);
        }
        bool IsEmpty(string k) => existing is null || !existing.Has(k) || existing[k].IsEmptyValue();
        void Fill(string k, JsonNode v)
        {
            if (schema.Field(k) is not null && !values.ContainsKey(k) && IsEmpty(k)) values[k] = v;
        }
        var c = record.Context;
        Fill(config.MeetingDateField, AyaText.Iso(startedAt));
        Fill(config.MeetingExternalIdField, $"meetingassistant:{sessionID}");
        Fill(config.MeetingTitleField, c.Title);
        if (c.MeetingType is { } type && schema.TryCoerce(type, config.MeetingTypeField, out _)) Fill(config.MeetingTypeField, type);
        void Link(string? k, string? id)
        {
            if (k is null || id is null || schema.Field(k) is null) return;
            Fill(k, schema.LinkValue(k, id));
        }
        Link(config.MeetingClientField, c.ClientID);
        Link(config.MeetingProjectField, c.ProjectID);
        Link(config.MeetingTemplateField, c.PlaybookID);
        if (config.MeetingParticipantsField is { } pk && schema.Field(pk) is { } pf && memberIDs.Count > 0)
        {
            var current = existing?.LinkedIDs(pk) ?? new List<string>();
            var all = current.Concat(memberIDs.Where(m => !current.Contains(m))).ToList();
            if (!all.SequenceEqual(current))
                values[pk] = pf.Kind == AyaFieldKind.Relations ? Strings(all) : JsonValue.Create(all[0]);
        }
        return values;
    }

    /// <summary>The prompt for completing the tables (spec 02 §7.6).</summary>
    public static string TablesPrompt(AssistRecord record, IReadOnlyList<string> transcript, IReadOnlyList<TemplateTable> tables)
    {
        var p = ContextText(record);
        if (record.Context.TemplateNotes is { Count: > 0 } notes) p += "\n\nTEMPLATE NOTES:\n" + string.Join("\n", notes.Select(n => $"- {n}"));
        p += "\n\n" + string.Join("\n", tables.Select(t => AssistPrompts.TableText(t, filledOnly: false)));
        p += "\n\nTRANSCRIPT:\n" + string.Join("\n", transcript);
        p += "\n\nComplete the tables from the whole transcript. Return one entry per cell that the meeting answered: fill empty cells, correct values from the live notes that turned out wrong, leave cells nobody talked about out. Keep values short, in the meeting's language. For a row named \"…:\" (e.g. \"Eigener Vorschlag:\") put the proposal into the name column. Values marked \"(set by user)\" are final; don't return them.";
        return p;
    }

    /// <summary>The structured-output schema for completing the tables (spec 02 §7.6).</summary>
    public static JsonObject TablesSchema(IReadOnlyList<TemplateTable> tables)
    {
        var columnNames = tables.SelectMany(t => t.Columns).Distinct().OrderBy(x => x, StringComparer.Ordinal);
        var rowIDs = tables.SelectMany(t => t.Rows.Select(r => r.Id));
        return new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = Strings(new[] { "cells" }),
            ["properties"] = new JsonObject
            {
                ["cells"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object", ["additionalProperties"] = false,
                        ["required"] = Strings(new[] { "row", "column", "value", "evidence" }),
                        ["properties"] = new JsonObject
                        {
                            ["row"] = new JsonObject { ["enum"] = Strings(rowIDs) },
                            ["column"] = new JsonObject { ["enum"] = Strings(columnNames) },
                            ["value"] = new JsonObject { ["type"] = "string" },
                            ["evidence"] = new JsonObject { ["type"] = "string", ["description"] = "short quote from the transcript" },
                        },
                    },
                },
            },
        };
    }

    /// <summary>
    /// Completes the template's tables from the final transcript (spec 02 §7.6). Values the user set are
    /// kept; returns new tables with Claude's values and quotes.
    /// </summary>
    public static async Task<List<TemplateTable>> CompleteTablesAsync(string binary, string model, AssistRecord record, string sessionID,
                                                                      IReadOnlyList<TemplateTable> tables, CancellationToken ct = default)
    {
        if (tables.Count == 0) return tables.ToList();
        var transcript = TranscriptLines(sessionID, record.Lines);
        var r = await ClaudeCli.OneShotAsync(binary, AssistPrompts.TablesSystem, TablesPrompt(record, transcript, tables), model, "medium",
                                             TablesSchema(tables), ct: ct);
        return ApplyCells(r.Structured, tables);
    }

    /// <summary>Applies the returned cells with Claude's rules (user-set cells stay).</summary>
    public static List<TemplateTable> ApplyCells(JsonNode? structured, IReadOnlyList<TemplateTable> tables)
    {
        if (structured?["cells"] is not JsonArray cells) throw ClaudeException.Failed("no table values returned");
        var output = tables.Select(t => JsonIO.Deserialize<TemplateTable>(JsonSerializer.Serialize(t, JsonIO.Options))).ToList();
        foreach (var c in cells)
        {
            if (c?["row"].AsString() is not { } row || c["column"].AsString() is not { } col || c["value"].AsString() is not { } value) continue;
            var tid = row.Split('.')[0];
            output.FirstOrDefault(t => t.Id == tid)?.Set(row, col, value, c["evidence"].AsString());
        }
        return output;
    }

    /// <summary>
    /// Team members among the attendees (and the user), matched by name (spec 02 §7.5): full name, or a
    /// first name that matches exactly one member. Each member once.
    /// </summary>
    public static List<string> MemberIDs(IEnumerable<string> attendees, IReadOnlyList<AyaRecord> members)
    {
        var output = new List<string>();
        foreach (var a in attendees.Select(AssistText.Fold).Where(a => a.Length > 0))
        {
            var full = members.Where(m => AssistText.Fold(m.Title) == a).ToList();
            var first = members.Where(m => AssistText.Fold(m.Title).StartsWith(a + " ", StringComparison.Ordinal)).ToList();
            var hit = full.FirstOrDefault() ?? (first.Count == 1 ? first[0] : null);
            if (hit is not null && !output.Contains(hit.Id)) output.Add(hit.Id);
        }
        return output;
    }

    // MARK: CRM

    /// <summary>
    /// Fields of a CRM record Claude may change (spec 02 §7.8): writable content fields except dates, selects
    /// without options and the technical fields hidden from the live context.
    /// </summary>
    public static List<AyaField> EditableFields(AyaListSchema schema) =>
        schema.WritableContent.Where(f => f.Kind != AyaFieldKind.Datetime && f.Kind != AyaFieldKind.Date
                                          && !(f.Kind == AyaFieldKind.Select && f.Options.Count == 0) && !TechnicalFields.Contains(f.Key)).ToList();

    /// <summary>
    /// Values for creating a contact from a proposal: the ticked fields plus the link to the client, written
    /// as an id or a list of ids depending on the link field. Empty when no field is ticked.
    /// </summary>
    public static Dictionary<string, JsonNode?> NewContactValues(ContactProposal c, string? clientID, string? link, AyaListSchema schema)
    {
        var values = new Dictionary<string, JsonNode?>();
        foreach (var ch in c.Changes.Where(ch => ch.Include))
            if (schema.TryCoerce(ch.Value, ch.Key, out var v)) values[ch.Key] = v;
        if (values.Count == 0) return values;
        if (link is not null && clientID is not null) values[link] = schema.LinkValue(link, clientID);
        return values;
    }

    static readonly string[] NewContactWords =
        { "first_name", "last_name", "vorname", "nachname", "name__", "position", "rolle", "role", "email", "telefon", "phone", "title" };

    /// <summary>Fields asked for a new contact: editable, not boolean or select, named like a name, role, e-mail or phone.</summary>
    public static List<AyaField> NewContactFields(AyaListSchema schema) =>
        EditableFields(schema).Where(f => f.Kind != AyaFieldKind.Boolean && f.Kind != AyaFieldKind.Select
                                          && NewContactWords.Any(w => f.Key.StartsWith(w, StringComparison.Ordinal) || f.Key.Contains(w))).ToList();

    /// <summary>The CRM proposal prompt (spec 02 §7.8).</summary>
    public static string CrmPrompt(AssistRecord record, IReadOnlyList<string> transcript, AyaRecord client, IReadOnlyList<AyaRecord> contacts,
                                   IReadOnlyList<AyaField> clientFields, IReadOnlyList<AyaField> contactFields)
    {
        static string Current(AyaRecord r, IEnumerable<AyaField> fields) => string.Join("\n", fields.Select(f =>
            $"  {f.Key} ({f.Label}{(f.Kind == AyaFieldKind.Select ? "; one of: " + string.Join(", ", f.Options) : "")}): {AssistPrompts.Clip(r[f.Key].Describe(), 1500)}"));
        var p = $"CLIENT RECORD {client.Title} — current values:\n" + Current(client, clientFields);
        if (contacts.Count > 0)
        {
            p += "\n\nKNOWN CONTACTS — current values:\n";
            foreach (var c in contacts) p += $"contact_id {c.Id} — {c.Title}:\n" + Current(c, contactFields) + "\n";
        }
        p += "\n\nATTENDEES SEEN BY THE ASSISTANT:\n" + string.Join("\n", record.State.Attendees.Select(a =>
            $"- {a.Name}{(a.Role is { } r ? $", {r}" : "")}{(a.Company is { } co ? $", {co}" : "")}{(a.ContactID is { } id ? $" [contact_id {id}]" : " [not in CRM]")}{(a.Notes is { } n ? $": {n}" : "")}"));
        p += "\n\nMEETING SUMMARY:\n" + string.Join("\n", record.State.Summary.Select(s => $"- {s}"));
        p += "\n\nTRANSCRIPT:\n" + string.Join("\n", transcript);
        p += "\n\nPropose updates: client_changes for the client record, contact_changes for known contacts, new_contacts for external attendees who are not in the CRM yet (only real people from the client side, not the user). Use empty arrays when nothing should change.";
        return p;
    }

    /// <summary>The structured-output schema of the CRM proposal (spec 02 §7.8).</summary>
    public static JsonObject CrmSchema(IReadOnlyList<AyaField> clientFields, IReadOnlyList<AyaField> contactFields, IReadOnlyList<AyaField> newFields,
                                       IReadOnlyList<string> contactIDs)
    {
        JsonObject ChangeSchema(IEnumerable<AyaField> fields, bool withContact)
        {
            var props = new JsonObject
            {
                ["field"] = new JsonObject { ["enum"] = Strings(fields.Select(f => f.Key)) },
                ["new_value"] = new JsonObject { ["type"] = "string" },
                ["reason"] = new JsonObject { ["type"] = "string" },
            };
            var req = new List<string> { "field", "new_value", "reason" };
            if (withContact) { props["contact_id"] = new JsonObject { ["enum"] = Strings(contactIDs) }; req.Add("contact_id"); }
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = Strings(req), ["additionalProperties"] = false },
            };
        }
        var top = new JsonObject
        {
            ["client_changes"] = clientFields.Count == 0 ? new JsonObject { ["type"] = "array", ["maxItems"] = 0 } : ChangeSchema(clientFields, false),
        };
        if (contactIDs.Count > 0 && contactFields.Count > 0) top["contact_changes"] = ChangeSchema(contactFields, true);
        if (newFields.Count > 0)
        {
            var nprops = new JsonObject();
            foreach (var f in newFields) nprops[f.Key] = new JsonObject { ["type"] = Strings(new[] { "string", "null" }), ["description"] = f.Label };
            top["new_contacts"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object", ["properties"] = nprops, ["required"] = Strings(newFields.Select(f => f.Key)), ["additionalProperties"] = false,
                },
            };
        }
        var keys = top.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return new JsonObject { ["type"] = "object", ["properties"] = top, ["required"] = Strings(keys), ["additionalProperties"] = false };
    }

    /// <summary>
    /// Asks Claude which CRM fields to update and which attendees to add as contacts (spec 02 §7.8). Runs
    /// only for meetings with a client; the records passed are fresh from Aya.
    /// </summary>
    public static async Task<CRMProposal> ProposeCrmAsync(string binary, string model, AssistRecord record, string sessionID, AyaRecord client,
                                                          IReadOnlyList<AyaRecord> contacts, AyaListSchema clientSchema, AyaListSchema? contactSchema,
                                                          CancellationToken ct = default)
    {
        var cFields = EditableFields(clientSchema);
        var pFields = contactSchema is null ? new List<AyaField>() : EditableFields(contactSchema);
        var newFields = contactSchema is null ? new List<AyaField>() : NewContactFields(contactSchema);
        var transcript = TranscriptLines(sessionID, record.Lines);
        var prompt = CrmPrompt(record, transcript, client, contacts, cFields, pFields);
        var schema = CrmSchema(cFields, pFields, newFields, contacts.Select(c => c.Id).ToList());
        var r = await ClaudeCli.OneShotAsync(binary, AssistPrompts.CrmSystem, prompt, model, "medium", schema, ct: ct);
        return ReadCrm(r.Structured, client, contacts, clientSchema, contactSchema);
    }

    /// <summary>Turns Claude's structured answer into a proposal (spec 02 §7.8 "Result").</summary>
    public static CRMProposal ReadCrm(JsonNode? structured, AyaRecord client, IReadOnlyList<AyaRecord> contacts, AyaListSchema clientSchema,
                                     AyaListSchema? contactSchema)
    {
        if (structured is not JsonObject output) throw ClaudeException.Failed("no structured CRM proposal returned");
        ProposedValue? Proposed(JsonNode? item, AyaListSchema schema, AyaRecord rec)
        {
            if (item?["field"].AsString() is not { } key || schema.Field(key) is not { } f) return null;
            var nv = item["new_value"].Describe();
            var old = rec[key].Describe();
            if (nv == old || !schema.TryCoerce(nv, key, out _)) return null;
            return new ProposedValue
            {
                Key = key, Label = f.Label, Kind = f.Kind, Options = f.Options.ToList(), Old = old, Value = nv,
                Reason = item["reason"].AsString(), Include = false,
            };
        }
        var proposal = new CRMProposal
        {
            Client = (output["client_changes"] as JsonArray ?? new JsonArray()).Select(i => Proposed(i, clientSchema, client))
                .Where(x => x is not null).Select(x => x!).ToList(),
        };
        if (contactSchema is not null)
        {
            var byContact = new Dictionary<string, List<ProposedValue>>();
            foreach (var item in output["contact_changes"] as JsonArray ?? new JsonArray())
            {
                if (item?["contact_id"].AsString() is not { } cid || contacts.FirstOrDefault(c => c.Id == cid) is not { } c) continue;
                if (Proposed(item, contactSchema, c) is not { } p) continue;
                if (!byContact.TryGetValue(cid, out var list)) byContact[cid] = list = new();
                list.Add(p);
            }
            foreach (var c in contacts)
                if (byContact.TryGetValue(c.Id, out var ch))
                    proposal.Contacts.Add(new ContactProposal { ContactID = c.Id, Name = c.Title, Changes = ch, Include = false });
            var newFields = NewContactFields(contactSchema);
            foreach (var item in output["new_contacts"] as JsonArray ?? new JsonArray())
            {
                var changes = newFields.Select(f =>
                {
                    var v = item?[f.Key].Describe() ?? "";
                    return v.Length == 0 ? null : new ProposedValue
                    {
                        Key = f.Key, Label = f.Label, Kind = f.Kind, Options = new(), Old = "", Value = v, Reason = null, Include = true,
                    };
                }).Where(x => x is not null).Select(x => x!).ToList();
                if (changes.Count == 0) continue;
                var name = string.Join(" ", changes.Where(x => x.Key.Contains("name")).Select(x => x.Value));
                proposal.Contacts.Add(new ContactProposal { ContactID = null, Name = name.Length == 0 ? changes[0].Value : name, Changes = changes, Include = false });
            }
        }
        return proposal;
    }

    /// <summary>The context text shared by the recap, tables and CRM prompts (spec 02 §7.3).</summary>
    public static string ContextText(AssistRecord r)
    {
        var c = r.Context;
        var s = "";
        if (c.ClientID is not null) s += $"CLIENT: {c.ClientTitle}\n";
        if (c.ProjectTitle is { } p) s += $"PROJECT: {p}\n";
        if (c.MeetingTitle is { } m) s += $"MEETING: {m}\n";
        if (c.PlaybookTitle is { } t) s += $"TEMPLATE: {t}\n";
        if (c.Goal.Length > 0) s += $"GOAL: {c.Goal}\n";
        s += $"USER (\"Me\" in the transcript): {c.MyName}\n";
        s += "\nATTENDEES:\n" + string.Join("\n", r.State.Attendees.Select(a =>
            $"- {a.Name}{(a.Role is { } ro ? $", {ro}" : "")}{(a.Company is { } co ? $", {co}" : "")}"));
        s += "\n\nLIVE SUMMARY:\n" + string.Join("\n", r.State.Summary.Select(x => $"- {x}"));
        s += "\n\nOPEN POINTS:\n" + string.Join("\n", r.State.OpenPoints.Select(o => $"- [{AssistPrompts.Raw(o.Status)}] {o.Text}"));
        s += "\n\nQUESTIONS ASKED:\n" + string.Join("\n", r.State.AskedQuestions.Select(q => $"- {q.Text}"));
        return s;
    }
}
