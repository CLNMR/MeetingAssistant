using System.Text.Json;
using System.Text.Json.Nodes;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;
using Xunit;
using static MeetingAssistant.Core.Tests.Assist.AssistTestData;

namespace MeetingAssistant.Core.Tests.Assist;

// Port of Tests/MeetingAssistantCoreTests/AssistTests.swift.

public class AssistOpTests
{
    [Fact]
    public void ParsesEveryOp()
    {
        Assert.Equal(new AssistOp.NextQuestions(new() { new Question { Id = "Q1", Text = "Wer entscheidet?", Why = "Entscheider fehlt", PlaybookItem = "P3.3" } }),
                     AssistOp.Parse("""{"op":"next_questions","items":[{"text":"Wer entscheidet?","why":"Entscheider fehlt","playbook_item":"P3.3"}]}"""));
        Assert.Equal(new AssistOp.SummaryAppend(new() { "A", "B" }), AssistOp.Parse("""{"op":"summary_append","bullets":["A","  ","B"]}"""));
        Assert.Equal(new AssistOp.SummaryReplace(2, "X"), AssistOp.Parse("""{"op":"summary_replace","index":2,"text":"X"}"""));
        Assert.Equal(new AssistOp.SetOpenPoint(new OpenPoint { Id = "O1", Text = "Budget", Status = OpenPointStatus.Answered }),
                     AssistOp.Parse("""{"op":"open_point","id":"O1","text":"Budget","status":"answered"}"""));
        Assert.Equal(new AssistOp.SetPlaybook("P1.2", PlaybookStatus.Covered, null), AssistOp.Parse("""{"op":"playbook","item":"P1.2","status":"covered"}"""));
        var a = Assert.IsType<AssistOp.SetAttendee>(AssistOp.Parse("""{"op":"attendee","name":"Anna","speaker":"Speaker 2","present":true}""")).Attendee;
        Assert.True(a.Name == "Anna" && a.Speaker == "Speaker 2" && a.Present == true);
    }

    [Fact]
    public void IgnoresNoise()
    {
        foreach (var line in new[] { "", "```json", "Here are the updates:", """{"op":"unknown"}""", """{"op":"playbook","item":"P1","status":"done"}""", "{\"op\":\"summary_ap" })
            Assert.Null(AssistOp.Parse(line));
        Assert.Equal(new AssistOp.SummaryAppend(new() { "x" }), AssistOp.Parse("""{"op":"summary_append","bullets":["x"]},"""));   // trailing comma
    }

    [Fact]
    public void ApplyMergesAttendeesAndOpenPoints()
    {
        var s = new AssistState();
        s.Apply(new AssistOp.SetAttendee(new Attendee { Name = "Speaker 2", Speaker = "Speaker 2" }));
        s.Apply(new AssistOp.SetAttendee(new Attendee { Name = "Erika Musterfrau", Role = "GF", Speaker = "Speaker 2" }));
        Assert.Single(s.Attendees);
        Assert.True(s.Attendees[0].Name == "Erika Musterfrau" && s.Attendees[0].Role == "GF" && s.Attendees[0].Present == true);
        s.Apply(new AssistOp.SetOpenPoint(new OpenPoint { Id = "O1", Text = "Budget", RaisedBy = "Erika" }));
        s.Apply(new AssistOp.SetOpenPoint(new OpenPoint { Id = "O1", Text = "Budget 2027", Status = OpenPointStatus.Answered }));
        Assert.True(s.OpenPoints.Count == 1 && s.OpenPoints[0].RaisedBy == "Erika" && s.OpenPoints[0].Status == OpenPointStatus.Answered);
        s.Playbook = new() { new PlaybookItem("P1.1", "A", "x") };
        Assert.Equal(new[] { "p:P1.1" }, s.Apply(new AssistOp.SetPlaybook("P1.1", PlaybookStatus.Partial, "e")));
        Assert.Empty(s.Apply(new AssistOp.SetPlaybook("P9", PlaybookStatus.Covered, null)));
    }
}

public class PlaybookParserTests
{
    [Fact]
    public void SectionsAndBullets()
    {
        var md = "### 1. Vorbereitung\n\n- Firma recherchieren\n- Ziel festlegen\n\n### 2. Bedarf ermitteln\n1. Welche Herausforderungen gibt es?\n* Wer entscheidet mit?";
        var items = PlaybookParser.Items(md);
        Assert.Equal(new[] { "P1.1", "P1.2", "P2.1", "P2.2" }, items.Select(i => i.Id));
        Assert.True(items[0].Section == "Vorbereitung" && items[2].Text == "Welche Herausforderungen gibt es?");
    }

    [Fact]
    public void FlatList() => Assert.Equal(new[] { "P1", "P2", "P3" }, PlaybookParser.Items("- a\n- b\nSome prose\n- c").Select(i => i.Id));
}

public class AyaSchemaTests
{
    public const string OpenApi = """
        {"paths": {"/api/workspaces/W/records/pmMeetings": {"post": {"requestBody": {"content": {"application/json": {"schema": {"$ref": "#/components/schemas/PmMeetingsCreateBody"}}}}}}},
         "components": {"schemas": {
           "PmMeetingsCreateBody": {"properties": {"values": {"properties": {}}}},
           "PmMeetingsUpdateBody": {"properties": {"values": {"properties": {
              "title__value": {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "Text content"},
              "summary__value": {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "GitHub-Flavored Markdown text"},
              "meeting_type__value": {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "Selected option techName"},
              "meeting_date__value": {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "ISO datetime string"},
              "participants__ids": {"type": "array", "description": "Referenced record ULIDs (insertion order)"},
              "project__id": {"anyOf": [{"type": "string"}, {"type": "null"}], "description": "Referenced record ULID"},
              "done__value": {"type": "boolean", "description": "Boolean value"},
              "count__value": {"anyOf": [{"type": "number"}, {"type": "null"}], "description": "Numeric value"}}}}},
           "PmMeetingsRecord": {"properties": {
              "id": {"type": "string"}, "title__value": {}, "task_count__value": {"anyOf": [{"type": "number"}, {"type": "null"}], "description": "Numeric value"},
              "project__computed_title": {"readOnly": true}}}}}}
        """;

    static byte[] Doc => System.Text.Encoding.UTF8.GetBytes(OpenApi);

    [Fact]
    public void ParsesKindsAndWritability()
    {
        var s = AyaListSchema.Parse(Doc, "W", "pmMeetings");
        Assert.Equal(AyaFieldKind.Markdown, s.Field("summary__value")?.Kind);
        Assert.Equal(AyaFieldKind.Select, s.Field("meeting_type__value")?.Kind);
        Assert.Equal(AyaFieldKind.Datetime, s.Field("meeting_date__value")?.Kind);
        Assert.Equal(AyaFieldKind.Relations, s.Field("participants__ids")?.Kind);
        Assert.Equal(AyaFieldKind.Relation, s.Field("project__id")?.Kind);
        Assert.Equal(AyaFieldKind.Boolean, s.Field("done__value")?.Kind);
        Assert.False(s.Field("task_count__value")?.Writable);
        Assert.Null(s.Field("project__computed_title"));
        Assert.Equal("Meeting type", s.Field("meeting_type__value")?.Label);
        Assert.Equal("kundePraefix", new AyaField("kunde_praefix__value", AyaFieldKind.Text, true).BrickTechName);
        Assert.Equal(s.Fields.Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal), s.Fields.Select(f => f.Key));

        s.LearnOptions(new[] { Rec(("meeting_type__value", "customer")), Rec(("meeting_type__value", "internal")) });
        Assert.Equal(new[] { "customer", "internal" }, s.Field("meeting_type__value")?.Options);
        Assert.True(s.TryCoerce("customer", "meeting_type__value", out var v) && v.AsString() == "customer");
        Assert.False(s.TryCoerce("sales", "meeting_type__value", out _));
        Assert.True(s.TryCoerce("ja", "done__value", out v) && v.AsBool() == true);
        Assert.True(s.TryCoerce("1,5", "count__value", out v) && v.AsNumber() == 1.5);
        Assert.False(s.TryCoerce("x", "nope__value", out _));
    }

    [Fact]
    public void UnknownListIsReported() =>
        Assert.Throws<AyaException>(() => AyaListSchema.Parse(Doc, "W", "firma"));

    [Fact]
    public void QueryJsonShape()
    {
        var q = new AyaQuery { Filter = new("firma", "01ABC", "id"), SortField = "system", SortValueKey = "updatedAt", Descending = true, Limit = 5 };
        var j = q.Json();
        Assert.Equal("01ABC", j["filter"]?["value"].AsString());          // a plain string, not {"value": …}
        Assert.Equal("constant", j["filter"]?["valueType"].AsString());
        Assert.True(((JsonArray)j["sorting"]!)[0]!["descending"].AsBool());
        Assert.Equal("""{"filter":{"brickTechName":"firma","brickValueKey":"id","comparator":"equal","nodeType":"condition","value":"01ABC","valueType":"constant"},"pagination":{"limit":5,"offset":0},"sorting":[{"brickTechName":"system","brickValueKey":"updatedAt","descending":true}]}""",
                     JsonIO.Compact(j));
        Assert.Equal("""{"filter":null,"pagination":{"limit":50,"offset":0},"sorting":[]}""", JsonIO.Compact(new AyaQuery().Json()));
    }

    [Fact]
    public void SearchNameDropsLegalForm()
    {
        Assert.Equal("MUSTER SOLAR", AyaService.SearchName("MUSTER SOLAR GmbH & Co. KG"));
        Assert.Equal("Beispilop", AyaService.SearchName("Beispilop GmbH"));
    }
}

public class RecapSchemaTests
{
    [Fact]
    public void RecapFieldsSkipAppFilledAndUnknownSelects()
    {
        var s = new AyaListSchema("W", "pmMeetings", new[]
        {
            new AyaField("title__value", AyaFieldKind.Text, true),
            new AyaField("summary__value", AyaFieldKind.Markdown, true),
            new AyaField("meeting_date__value", AyaFieldKind.Datetime, true),
            new AyaField("external_meeting_id__value", AyaFieldKind.Text, true),
            new AyaField("meeting_type__value", AyaFieldKind.Select, true, new[] { "customer" }),   // from the template
            new AyaField("status__value", AyaFieldKind.Select, true, new[] { "done" }),
            new AyaField("mood__value", AyaFieldKind.Select, true),
            new AyaField("project__id", AyaFieldKind.Relation, true),
        });
        var fields = RecapBuilder.RecapFields(s, new AyaConfig());
        Assert.Equal(new[] { "title__value", "summary__value", "status__value" }, fields.Select(f => f.Key));
        var schema = RecapBuilder.ObjectSchema(fields);
        Assert.Equal(3, ((JsonArray)schema["required"]!).Count);
        Assert.Equal("""{"description":"Status","enum":["done",null]}""", JsonIO.Compact(schema["properties"]!["status__value"]));
        Assert.Equal("""{"description":"Summary (markdown)","type":["string","null"]}""", JsonIO.Compact(schema["properties"]!["summary__value"]));
    }
}

[Collection("AppRoot")]
public class MeetingTargetTests
{
    [Fact]
    public void NewMeetingGetsLinksTypeDateAndParticipants()
    {
        var v = RecapBuilder.RecapValues(Fields(), Record(), "S", DateTime.UnixEpoch, MeetingSchema(), new AyaConfig(), memberIDs: new[] { "M1" });
        Assert.Equal("C1", v["customer__id"].AsString());
        Assert.Equal("P1", v["project__id"].AsString());
        Assert.Equal("T1", v["load_template__id"].AsString());
        Assert.Equal("customer", v["meeting_type__value"].AsString());
        Assert.Equal("""["M1"]""", JsonIO.Compact(v["participants__ids"]));
        Assert.Equal("1970-01-01T00:00:00Z", v["meeting_date__value"].AsString());
        Assert.Equal("meetingassistant:S", v["external_meeting_id__value"].AsString());
        Assert.Equal("Kickoff MUSTER SOLAR", v["title__value"].AsString());
    }

    [Fact]
    public void ExistingMeetingKeepsWhatItHas()
    {
        var existing = Rec(("id", "X"), ("title__value", "Kickoff (planned)"), ("customer__id", "C9"), ("project__id", null),
                           ("meeting_date__value", "2026-11-10T09:00:00+00:00"), ("meeting_type__value", "internal"),
                           ("participants__ids", new[] { "M0" }));
        // The planned title is kept unless the user ticks the title field.
        var f = RecapBuilder.Target(Fields(), existing);
        Assert.True(f[0].Old == "Kickoff (planned)" && !f[0].Include);
        Assert.True(f[1].Old.Length == 0 && f[1].Include);
        var v = RecapBuilder.RecapValues(f, Record(), "S", DateTime.UtcNow, MeetingSchema(), new AyaConfig(), existing, new[] { "M1", "M0" });
        Assert.False(v.ContainsKey("title__value"));
        Assert.Equal("- went well", v["summary__value"].AsString());
        Assert.False(v.ContainsKey("customer__id"));                 // already linked elsewhere: left alone
        Assert.Equal("P1", v["project__id"].AsString());              // empty: filled
        Assert.False(v.ContainsKey("meeting_date__value"));
        Assert.False(v.ContainsKey("meeting_type__value"));
        Assert.Equal("""["M0","M1"]""", JsonIO.Compact(v["participants__ids"]));
    }

    [Fact]
    public void MembersMatchByFullOrUniqueFirstName()
    {
        var members = new List<AyaRecord>
        {
            Rec(("id", "1"), ("computedTitle", "Erika Musterfrau")), Rec(("id", "2"), ("computedTitle", "Lena Beispiel")),
            Rec(("id", "3"), ("computedTitle", "Max Muster")), Rec(("id", "4"), ("computedTitle", "Max Beispiel")),
        };
        Assert.Equal(new[] { "1", "2" }, RecapBuilder.MemberIDs(new[] { "erika musterfrau", "Lena", "Max", "Someone Else" }, members));
        // Diacritics and spacing are folded.
        Assert.Equal(new[] { "1" }, RecapBuilder.MemberIDs(new[] { "Érika   Musterfrau" }, members));
    }

    [Fact]
    public void TitleFallsBackFromMeetingToClientAndTemplate()
    {
        var c = Record().Context;
        Assert.Equal("MUSTER SOLAR – Projekt-Kickoff", c.Title);
        c.ClientID = null;
        Assert.Equal("P0001 – MUSTER SOLAR rollout – Projekt-Kickoff", c.Title);
        c.MeetingTitle = "Weekly";
        Assert.Equal("Weekly", c.Title);
        Assert.Equal("Meeting", new AssistContext().Title);
    }

    [Fact]
    public void OldAssistRecordsStillLoad()
    {
        // assist.json from before templates: clientID always set, no project/meeting keys.
        var json = JsonSerializer.SerializeToNode(Record().Context, JsonIO.Options)!.AsObject();
        foreach (var k in new[] { "projectID", "projectTitle", "projectFieldList", "meetingID", "meetingTitle", "meetingType" }) json.Remove(k);
        var c = JsonIO.Deserialize<AssistContext>(json.ToJsonString());
        Assert.True(c.ClientID == "C1" && c.ProjectID is null && c.MeetingID is null);
    }
}

public class AyaConfigMigrationTests
{
    [Fact]
    public void LegacyConfigKeepsConnectionAndClientLists()
    {
        var legacy = """{"orgId":"ORG","contactsClientField":"firma__id","recapClientField":null,"lists":{"clients":{"workspaceId":"W1","workspaceLabel":"CRM","techName":"firma","label":"Companies"},"playbooks":{"workspaceId":"W2","workspaceLabel":"Wiki","techName":"wikiPlaybooks","label":"Playbooks"},"recaps":{"workspaceId":"W3","workspaceLabel":"PM","techName":"pmMeetings","label":"Meetings"}}}""";
        var c = AyaConfig.Migrate(legacy);
        Assert.Equal("ORG", c.OrgId);
        Assert.Equal("firma", c.List(AyaSlot.Clients)?.TechName);
        Assert.True(c.List(AyaSlot.Meetings) is null && c.List(AyaSlot.Templates) is null);   // picked again from Aya
        Assert.False(c.Lists.ContainsKey("playbooks") || c.Lists.ContainsKey("recaps"));
        Assert.False(c.IsComplete);
        Assert.Equal("customer__id", c.MeetingClientField);
    }

    [Fact]
    public void ExplicitNoLinkSurvivesSaving()
    {
        var c = new AyaConfig { MeetingProjectField = null };
        var back = AyaConfig.FromJson(c.ToJson());
        Assert.Null(back.MeetingProjectField);
        Assert.Equal("customer__id", back.MeetingClientField);
        // A missing key means the default.
        Assert.Equal("participants__ids", AyaConfig.FromJson("""{"orgId":"o"}""").MeetingParticipantsField);
        Assert.Contains("\"meetingProjectField\":null", c.ToJson());
    }

    [Fact]
    public void LoadsFromPreferencesWithLegacyFallback()
    {
        var prefs = new MemoryPreferences();
        Assert.Equal("", AyaConfig.Load(prefs).OrgId);
        prefs.SetString(AyaConfig.LegacyPreferencesKey, """{"orgId":"ORG"}""");
        Assert.Equal("ORG", AyaConfig.Load(prefs).OrgId);
        new AyaConfig { OrgId = "NEW" }.Save(prefs);
        Assert.Equal("NEW", AyaConfig.Load(prefs).OrgId);
    }
}

public class AssistIngestTests
{
    static LiveLine Line(string id, Track t, double start, string text, string? spk, bool final) => new(id, t, start, start + 1, text, spk, final);

    [Fact]
    public async Task SendsFinishedUtterancesOnceWithStableSpeakerNames()
    {
        var s = new AssistSettings { LabelWait = 60 };
        var e = new AssistEngine(Context(), s, FakeConversation.Factory(new()), (_, _) => { });
        // Not final yet → nothing.
        await e.IngestAsync(new[] { Line("mic-1.0", Track.Mic, 1, "Hallo", "me", false) });
        Assert.Empty(await e.GetTranscriptAsync());
        // Final, but the system line has no speaker yet → wait.
        await e.IngestAsync(new[] { Line("mic-1.0", Track.Mic, 1, "Hallo zusammen", "me", true), Line("system-1.0", Track.System, 2, "Hi", null, true) });
        Assert.Equal(new[] { "[00:00:01] Erika: Hallo zusammen" }, (await e.GetTranscriptAsync()).Select(l => l.Rendered));
        // Label arrives; the utterance is split into two speakers.
        await e.IngestAsync(new[]
        {
            Line("mic-1.0", Track.Mic, 1, "Hallo zusammen", "me", true), Line("system-1.0", Track.System, 2, "Hi", "system-3", true),
            Line("system-1.1", Track.System, 3, "und hallo", "system-5", true),
        });
        await e.IngestAsync(new[] { Line("system-1.0", Track.System, 2, "Hi", "system-3", true) });   // repeated publish
        Assert.Equal(new[] { "[00:00:01] Erika: Hallo zusammen", "[00:00:02] Speaker 1: Hi", "[00:00:03] Speaker 2: und hallo" },
                     (await e.GetTranscriptAsync()).Select(l => l.Rendered));
    }

    [Fact]
    public async Task UnlabelledLinesAreSentAfterTheLabelWait()
    {
        var clock = new ManualClock();
        var e = new AssistEngine(Context(), new AssistSettings(), FakeConversation.Factory(new()), (_, _) => { }, clock);
        await e.IngestAsync(new[] { Line("system-4.0", Track.System, 5, "Hallo", null, true) });
        Assert.Empty(await e.GetTranscriptAsync());
        clock.Advance(TimeSpan.FromSeconds(2.9));
        await e.IngestAsync(new[] { Line("system-4.0", Track.System, 5, "Hallo", null, true) });
        Assert.Empty(await e.GetTranscriptAsync());
        clock.Advance(TimeSpan.FromSeconds(0.1));     // labelWait 3 s
        await e.IngestAsync(new[] { Line("system-4.0", Track.System, 5, "Hallo", null, true) });
        Assert.Equal(new[] { "[00:00:05] Them (?): Hallo" }, (await e.GetTranscriptAsync()).Select(l => l.Rendered));
    }
}

public class TemplateTableTests
{
    [Fact]
    public void ParsesItemsTablesAndNotes()
    {
        var p = TemplateParser.Parse(TemplateMarkdown);
        // The agenda overview only repeats the headings, so it is no checklist of its own.
        Assert.DoesNotContain(p.Items, i => i.Section == "Agenda");
        Assert.Contains("Per module in use: Top three features?", p.Items.Select(i => i.Text));
        Assert.Contains("Per module in use: Biggest benefit?", p.Items.Select(i => i.Text));
        var t = Assert.Single(p.Tables);
        Assert.True(t.Id == "T1" && t.Section == "Systems");
        Assert.Equal("Which modules do you use today, and which matter to you?", t.Prompt);
        Assert.Equal(new[] { 2, 3 }, t.AnswerColumns);
        Assert.True(t.Rows.Count == 4 && t.Rows[0].Id == "T1.1");
        Assert.Equal("Sales · CRM", t.Label(t.Rows[0]));
        Assert.Equal(new[] { "Delivery model: Our idea: standard modules that you adapt yourselves." }, p.Notes);
    }

    [Fact]
    public void CellsRespectUserAndOpenRows()
    {
        var t = TemplateParser.Parse(TemplateMarkdown).Tables[0];
        Assert.True(t.Set("T1.1", "Used today (system)", "Pipedrive", "we use Pipedrive"));
        Assert.True(t.Set("T1.1", "relevant", "ja"));                     // column name, case-insensitive
        Assert.True(t.Set("T1.2", "Relevant", "nein", byUser: true));
        Assert.False(t.Set("T1.2", "Relevant", "ja"));                    // Claude keeps the user's value
        Assert.False(t.Set("T1.1", "Module", "ERP"));                     // fixed names stay
        Assert.True(t.Set("T1.4", "Area", "Dispatch board"));             // "Own idea:" gets its name
        Assert.Equal("Own idea: Dispatch board", t.Rows[3].Cells[0]);
        Assert.False(t.Set("T1.4", "Area", "Other"));                     // Claude fills an open key only once
        Assert.True(t.Set("T1.4", "Area", "Other", byUser: true));        // the user any time
        Assert.Equal("we use Pipedrive", t.Rows[0].Evidence["Used today (system)"]);
        Assert.Equal(3, t.FilledCount);                                   // the proposal row counts as answered
        Assert.Equal(2, t.ColumnNamed("Used today"));                     // prefix match
    }

    [Fact]
    public void LongTablesOnlyWriteAnsweredRows()
    {
        var t = new TemplateTable("T2", "AI", "", new[] { "Module", "Use case", "Rating" }, new[] { 2 },
                                  Enumerable.Range(1, 40).Select(i => new TemplateTable.Row($"T2.{i}", new[] { $"M{i}", $"U{i}", "" })));
        t.Set("T2.7", "Rating", "4");
        var md = t.Markdown();
        Assert.Contains("| M7 | U7 | 4 |", md);
        Assert.DoesNotContain("| M8 |", md);
        Assert.Contains("39 rows not discussed", md);
        // Up to 30 rows: all rows.
        var shortT = new TemplateTable("T3", "", "", new[] { "A", "B" }, new[] { 1 }, Enumerable.Range(1, 30).Select(i => new TemplateTable.Row($"T3.{i}", new[] { $"a{i}", "" })));
        Assert.DoesNotContain("not discussed", shortT.Markdown());
    }

    [Fact]
    public void SavingAgainReplacesTheBlock()
    {
        var t = TemplateParser.Parse(TemplateMarkdown).Tables[0];
        t.Set("T1.1", "Relevant", "ja");
        var block1 = TemplateParser.Block("Discovery", new[] { t })!;
        var once = TemplateParser.Merge("## Summary\n- good", block1);
        t.Set("T1.3", "Relevant", "nein");
        var block2 = TemplateParser.Block("Discovery", new[] { t })!;
        var twice = TemplateParser.Merge(once + "\n\n## Later\nkept", block2);
        Assert.Equal(2, twice.Split("## Discovery").Length);
        Assert.Contains("| Stock | Warehouse |  | nein |", twice);
        Assert.True(twice.StartsWith("## Summary\n- good") && twice.EndsWith("## Later\nkept"));
        Assert.Null(TemplateParser.Block("x", new[] { TemplateParser.Parse(TemplateMarkdown).Tables[0] }));
    }

    [Fact]
    public void ClaudeFillsCellsThroughOps()
    {
        var s = new AssistState { Tables = TemplateParser.Parse(TemplateMarkdown).Tables };
        var op = AssistOp.Parse("""{"op":"table_cell","row":"T1.3","column":"Used today (system)","value":"Excel","evidence":"alles in Excel"}""");
        Assert.Equal(new AssistOp.TableCell("T1.3", "Used today (system)", "Excel", "alles in Excel"), op);
        Assert.Equal(new[] { "t:T1.3" }, s.Apply(op!));
        Assert.Equal("Excel", s.Tables![0].Rows[2].Cells[2]);
        Assert.Empty(s.Apply(new AssistOp.TableCell("T9.1", "x", "y", null)));
    }

    [Fact]
    public void TablesGoToADedicatedFieldOrTheSummary()
    {
        AyaListSchema Schema(params AyaField[] extra) => new("W", "pmMeetings", new[]
        {
            new AyaField("title__value", AyaFieldKind.Text, true), new AyaField("summary__value", AyaFieldKind.Markdown, true),
            new AyaField("decisions__value", AyaFieldKind.Markdown, true),
        }.Concat(extra));
        var cfg = new AyaConfig();
        Assert.Equal("summary__value", cfg.TablesField(Schema()));
        var withNotes = Schema(new AyaField("protokoll__value", AyaFieldKind.Markdown, true));
        Assert.Equal("protokoll__value", cfg.TablesField(withNotes));
        Assert.DoesNotContain("protokoll__value", RecapBuilder.RecapFields(withNotes, cfg).Select(f => f.Key));
        cfg.MeetingTablesField = "decisions__value";
        Assert.Equal("decisions__value", cfg.TablesField(withNotes));
        cfg.MeetingTablesField = "-";
        Assert.Null(cfg.TablesField(withNotes));

        var rec = Record();
        rec.Context.PlaybookTitle = "Discovery";
        var t = TemplateParser.Parse(TemplateMarkdown).Tables[0];
        t.Set("T1.1", "Relevant", "ja");
        var v = RecapBuilder.RecapValues(Fields(), rec, "S", DateTime.UtcNow, MeetingSchema(), new AyaConfig(), tables: new[] { t });
        var summary = v["summary__value"].AsString() ?? "";
        Assert.StartsWith("- went well\n\n## Discovery", summary);
        Assert.Contains("| Sales | CRM |  | ja |", summary);
    }
}

public class AssistConsistencyTests
{
    static TemplateTable GermanTable() => new("T1", "Systeme", "Welche Module nutzen Sie heute und welche sind für Sie relevant?",
        new[] { "Bereich", "Modul", "Relevant für uns" }, new[] { 2 }, new[] { new TemplateTable.Row("T1.1", new[] { "Vertrieb", "CRM", "" }) });

    [Fact]
    public void SystemPromptCountsSixPanelsAndNamesRealEvents()
    {
        Assert.Contains("Keep six panels up to date", AssistPrompts.System);
        Assert.DoesNotContain("dismissed open point", AssistPrompts.System);
        Assert.Contains("[user set open point O2 to parked]", AssistPrompts.System);
    }

    [Fact]
    public void BooleanTableValuesUseTheMeetingLanguage()
    {
        var line = """{"op":"table_cell","row":"T1.1","column":"Relevant für uns","value":true}""";
        Assert.Equal(new AssistOp.TableCell("T1.1", "Relevant für uns", "yes", null), AssistOp.Parse(line));
        Assert.Equal(new AssistOp.TableCell("T1.1", "Relevant für uns", "ja", null), AssistOp.Parse(line, ("ja", "nein")));
        Assert.Equal(new AssistOp.TableCell("T1.1", "x", "nein", null), AssistOp.Parse("""{"op":"table_cell","row":"T1.1","column":"x","value":false}""", ("ja", "nein")));

        var c = Context();
        Assert.Equal(("yes", "no"), c.YesNoWords);                      // no signal: English
        c.TemplateTables = new() { GermanTable() };
        Assert.True(c.IsGerman && c.YesNoWords == ("ja", "nein"));
        c.TemplateTables = new() { new TemplateTable("T1", "Systems", "Which modules do you use today, and which matter to you?", new[] { "Area", "Module", "Relevant" }, new[] { 2 }, Array.Empty<TemplateTable.Row>()) };
        Assert.False(c.IsGerman);
        // Without a template the client's data decides.
        c.TemplateTables = null;
        c.ClientFieldList = new() { new LabeledValue("notiz__value", "Notiz", "Der Kunde nutzt heute ein eigenes System und ist mit dem Lager nicht zufrieden.") };
        Assert.True(c.IsGerman);
    }

    [Fact]
    public void OneListOfTechnicalFields()
    {
        var s = new AyaListSchema("W", "firma", new[] { "branche__value", "provenienz__value", "marketing_status__value", "owner_name__value", "lead_source__value", "kunde_praefix__value", "utm_campaign__value" }
            .Select(k => new AyaField(k, AyaFieldKind.Text, true)));
        var editable = RecapBuilder.EditableFields(s).Select(f => f.Key).ToList();
        Assert.Equal(new[] { "branche__value" }, editable);
        // The live context hides exactly the same fields.
        foreach (var f in s.Fields) Assert.Equal(!editable.Contains(f.Key), AssistContext.IsTechnical(f.Key));
        var r = new AyaRecord(new JsonObject(s.Fields.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)JsonValue.Create("x")))));
        Assert.Equal(new[] { "branche__value" }, AssistContext.LabeledValues(r, s).Select(l => l.Key));
    }

    [Fact]
    public void NewContactLinkFollowsTheFieldShape()
    {
        var fields = new[] { new AyaField("first_name__value", AyaFieldKind.Text, true) };
        var proposal = new ContactProposal
        {
            Name = "Max Beispiel", Include = true,
            Changes = new() { new ProposedValue { Key = "first_name__value", Label = "First name", Kind = AyaFieldKind.Text, Value = "Max", Include = true } },
        };
        var one = new AyaListSchema("W", "person", fields.Append(new AyaField("firma__id", AyaFieldKind.Relation, true)));
        var v1 = RecapBuilder.NewContactValues(proposal, "C1", "firma__id", one);
        Assert.Equal("Max", v1["first_name__value"].AsString());
        Assert.Equal("C1", v1["firma__id"].AsString());
        var many = new AyaListSchema("W", "person", fields.Append(new AyaField("firmen__ids", AyaFieldKind.Relations, true)));
        Assert.Equal("""["C1"]""", JsonIO.Compact(RecapBuilder.NewContactValues(proposal, "C1", "firmen__ids", many)["firmen__ids"]));
        // Unknown to the schema: judged by the key.
        Assert.Equal("""["C1"]""", JsonIO.Compact(one.LinkValue("other__ids", "C1")));
        // Nothing ticked: nothing to create.
        proposal.Changes[0].Include = false;
        Assert.Empty(RecapBuilder.NewContactValues(proposal, "C1", "firma__id", one));
    }

    [Fact]
    public void SpecsOfAllWorkspacesAreMerged()
    {
        AyaText.LanguageOverride = "en";
        try
        {
            AyaRecordSpec Spec(string id, string tech, string? ws, string label, bool? system = null) =>
                new() { Id = id, TechName = tech, WorkspaceId = ws, LocalizedLabel = new() { ["en"] = label }, IsSystem = system };
            var a = new AyaWorkspace { Id = "WA", Slug = "sales" };
            var b = new AyaWorkspace { Id = "WB", Slug = "projects" };
            var merged = AyaService.MergeSpecs(new[]
            {
                (a, new List<AyaRecordSpec> { Spec("1", "firma", "WA", "Companies"), Spec("2", "pmMeetings", "WB", "Meetings"), Spec("9", "user", "WA", "Users", true) }),
                (b, new List<AyaRecordSpec> { Spec("2", "pmMeetings", "WB", "Meetings"), Spec("3", "pmProjects", null, "Projects"), Spec("4", "firma", "WB", "Companies") }),
            });
            Assert.Equal(new[] { "Companies@WA", "Companies@WB", "Meetings@WB", "Projects@WB" }, merged.Select(s => $"{s.Label}@{s.WorkspaceId}"));
        }
        finally { AyaText.LanguageOverride = null; }
    }

    [Fact]
    public void ResumeMessageCarriesEventsAndDropsTheStartInstruction()
    {
        var c = Context();
        Assert.EndsWith(AssistPrompts.StartInstruction, AssistPrompts.ContextMessage(c));
        var st = new AssistState { Summary = new() { "Lager: zu klein" } };
        var m = AssistPrompts.ResumeMessage(c, st, new[] { "[00:00:01] Erika: Hallo" }, new[] { "user set open point O1 to parked" });
        Assert.DoesNotContain(AssistPrompts.StartInstruction, m);
        Assert.Contains("THE MEETING IS ALREADY RUNNING", m);
        Assert.Contains("[user set open point O1 to parked]", m);
        Assert.StartsWith(AssistPrompts.ContextBody(c), m);
    }

    [Fact]
    public void TimeoutHasItsOwnError() =>
        Assert.Equal("Claude did not answer within 600 s.", MeetingAssistant.Core.Claude.ClaudeException.TimedOut(600).Message);
}
