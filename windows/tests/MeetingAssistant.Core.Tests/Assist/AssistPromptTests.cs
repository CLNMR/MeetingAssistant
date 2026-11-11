using System.Text;
using System.Text.RegularExpressions;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;
using Xunit;
using static MeetingAssistant.Core.Tests.Assist.AssistTestData;

namespace MeetingAssistant.Core.Tests.Assist;

/// Exact prompt texts (spec 02 §3, §7).
public class AssistPromptTests
{
    /// The Mac sources, when this checkout has them (the repo holds both apps).
    static string? SwiftFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var p = Path.Combine(dir, relative);
            if (File.Exists(p)) return p;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// The value of a Swift multi-line string constant (`static let name = """ … """`).
    static string SwiftLiteral(string source, string name)
    {
        var m = Regex.Match(source, $@"static let {name} = """"""\n(.*?)\n( *)""""""", RegexOptions.Singleline);
        Assert.True(m.Success, name);
        var indent = m.Groups[2].Value.Length;
        var sb = new StringBuilder();
        var lines = m.Groups[1].Value.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Length >= indent ? lines[i][indent..] : lines[i].TrimStart(' ');
            if (l.EndsWith('\\')) sb.Append(l[..^1]);
            else { sb.Append(l); if (i < lines.Length - 1) sb.Append('\n'); }
        }
        return sb.ToString();
    }

    [Fact]
    public void SystemPromptsMatchTheMacCharacterForCharacter()
    {
        var path = SwiftFile("Sources/MeetingAssistantCore/Assist/AssistPrompts.swift");
        if (path is null) return;     // not in a full checkout
        var src = File.ReadAllText(path);
        Assert.Equal(AssistPrompts.System, SwiftLiteral(src, "system"));
        Assert.Equal(AssistPrompts.RecapSystem, SwiftLiteral(src, "recapSystem"));
        Assert.Equal(AssistPrompts.CatchUpSystem, SwiftLiteral(src, "catchUpSystem"));
        Assert.Equal(AssistPrompts.TablesSystem, SwiftLiteral(src, "tablesSystem"));
        Assert.Equal(AssistPrompts.CrmSystem, SwiftLiteral(src, "crmSystem"));
        Assert.Contains($"\"{AssistPrompts.StartInstruction}\"", src);
    }

    static AssistContext FullContext()
    {
        var p = TemplateParser.Parse("## 1. Einstieg\nZiel des Termins klären.\n- Ziel klären\n- Entscheider klären\n\n## 2. Systeme\nWelche Systeme?\n\n| Bereich | Heute |\n|---|---|\n| CRM | |\n");
        return new AssistContext
        {
            ClientID = "c1", ClientTitle = "MUSTER SOLAR GmbH",
            ClientFieldList = new() { new("branche__value", "Branche", "Solar\nund Speicher") },
            Contacts = new() { new ContactInfo { Id = "k1", Name = "Erika Musterfrau", Details = new() { new("position__value", "Position", "GF"), new("x", "Leer", "") } } },
            ProjectID = "p1", ProjectTitle = "P0001 Rollout", ProjectFieldList = new() { new("status__value", "Status", "aktiv") },
            MeetingID = "m1", MeetingTitle = "Kickoff",
            PlaybookID = "t1", PlaybookTitle = "Erstgespräch", PlaybookGoal = "Bedarf verstehen",
            PlaybookItems = p.Items, TemplateTables = p.Tables, TemplateNotes = p.Notes,
            Recaps = new() { new RecapSummary { Title = "Vorgespräch", Date = "2026-10-01", Fields = new() { new("summary__value", "Summary", "Gut") } } },
            Goal = "Angebot vorbereiten", Mode = MeetingMode.Call, MyName = "Lena Beispiel",
            Date = new DateTime(2026, 11, 10, 9, 30, 0, DateTimeKind.Local),
        };
    }

    [Fact]
    public void ContextMessageLayout()
    {
        var expected = """
            MEETING CONTEXT
            Date: 2026-11-10 09:30 · Type: Call meeting · The user is "Lena Beispiel" (the user is labelled Me; other voices, remote or in the room with them, are labelled by speaker).
            User's goal for this meeting: Angebot vorbereiten
            Meeting planned in Aya: Kickoff

            CLIENT: MUSTER SOLAR GmbH
            - Branche: Solar und Speicher

            PROJECT: P0001 Rollout
            - Status: aktiv

            KNOWN CONTACTS AT THIS CLIENT (possible attendees):
            - Erika Musterfrau [contact_id k1] — Position: GF

            MEETING TEMPLATE (agenda and questions; the checklist is the playbook): Erstgespräch
            Purpose: Bedarf verstehen
            Einstieg
              P1.1 Ziel klären
              P1.2 Entscheider klären

            TEMPLATE NOTES:
            - Einstieg: Ziel des Termins klären.

            TABLE T1 (section Systeme) — Welche Systeme?
            Columns: Bereich | [Heute]   (fill the columns in brackets)
              T1.1 CRM

            EARLIER MEETINGS (newest first):
            • 2026-10-01 Vorgespräch
              Summary: Gut

            The meeting is starting now. Give your first suggestions (opening questions based on the context and playbook) and the expected attendees.
            """;
        Assert.Equal(expected, AssistPrompts.ContextMessage(FullContext()));
        var c = Context();
        c.Mode = MeetingMode.InPerson;
        c.ClientID = null;
        Assert.StartsWith("MEETING CONTEXT\nDate: ", AssistPrompts.ContextBody(c));
        Assert.EndsWith("· Type: In person meeting · The user is \"Erika\".\n", AssistPrompts.ContextBody(c));
    }

    [Fact]
    public void ClipCutsWithAnEllipsis()
    {
        Assert.Equal("ab cd", AssistPrompts.Clip(" ab\ncd ", 10));
        Assert.Equal("abc…", AssistPrompts.Clip("abcdef", 3));
        Assert.Equal(1200 + 1, AssistPrompts.Clip(new string('x', 1300), 1200).Length);
    }

    [Fact]
    public void TableTextMarksAnswerColumnsAndUserValues()
    {
        var t = TemplateParser.Parse(TemplateMarkdown).Tables[0];
        t.Set("T1.1", "Used today (system)", "Pipedrive");
        t.Set("T1.1", "Relevant", "ja", byUser: true);
        Assert.Equal("""
            TABLE T1 (section Systems) — Which modules do you use today, and which matter to you?
            Columns: Area | Module | [Used today (system)] | [Relevant]   (fill the columns in brackets)
              T1.1 Sales · CRM → Used today (system)=Pipedrive; Relevant=ja (set by user)

            """.Replace("\r", ""), AssistPrompts.TableText(t, filledOnly: true));
        Assert.Equal(6, AssistPrompts.TableText(t, filledOnly: false).Split('\n').Length - 1);
    }

    [Fact]
    public void TickMessages()
    {
        Assert.Equal("CURRENT SUMMARY (rewrite a paragraph with summary_replace when the new lines add to its topic):\n  [0] Lager: klein\n  [1] Budget: 20k\nTRANSCRIPT (new):\n[00:01:05] Me: Hallo\n[user asks: suggest a question now]",
                     AssistPrompts.TickMessage(new[] { "[00:01:05] Me: Hallo" }, new[] { "user asks: suggest a question now" }, new[] { "Lager: klein", "Budget: 20k" }));
        Assert.Equal("[a]\n[b]", AssistPrompts.TickMessage(Array.Empty<string>(), new[] { "a", "b" }, new[] { "x" }));
    }

    [Fact]
    public void ResumeMessageLayout()
    {
        var c = Context();
        var st = new AssistState
        {
            Summary = new() { "Lager: klein" },
            OpenPoints = new() { new OpenPoint { Id = "O1", Text = "Budget", Status = OpenPointStatus.Parked } },
            Attendees = new() { new Attendee { Name = "Max Beispiel", Role = "Einkauf", Speaker = "Speaker 2" }, new Attendee { Name = "Lena" } },
            Playbook = new() { new PlaybookItem("P1", "", "x") { Status = PlaybookStatus.Partial } },
            AskedQuestions = new() { new Question { Id = "q", Text = "Wie groß?" } },
        };
        var m = AssistPrompts.ResumeMessage(c, st, new[] { "[00:00:01] Erika: Hallo" });
        Assert.Equal(AssistPrompts.ContextBody(c) + "\n" + """

            THE MEETING IS ALREADY RUNNING. Current panels (continue from here, reuse the ids):
            Summary so far (one paragraph per topic):
              [0] Lager: klein
            Open points:
              O1 (parked) Budget
            Attendees:
              Max Beispiel, Einkauf = Speaker 2
              Lena
            Playbook status:
              P1 partial
            Questions already asked:
              - Wie groß?

            TRANSCRIPT (last minutes):
            [00:00:01] Erika: Hallo

            Continue: give the next questions and any changes.
            """, m);
    }

    [Fact]
    public void CatchUpTakesTheLastMinutes()
    {
        var lines = new[] { new AssistLine(0, "Max", "alt"), new AssistLine(200, "Max", "neu"), new AssistLine(320, "Me", "jetzt") };
        Assert.Equal("MEETING: Kickoff\nUSER (\"Me\" in the transcript): Erika\n\nTRANSCRIPT OF THE LAST 2 MINUTES:\n[00:03:20] Max: neu\n[00:05:20] Me: jetzt",
                     AssistPrompts.CatchUpPrompt(lines, 2, "Erika", "Kickoff"));
        Assert.Null(AssistPrompts.CatchUpPrompt(Array.Empty<AssistLine>(), 5, "Erika", "x"));
    }

    [Fact]
    public void RenderedLinesUseWholeSeconds() =>
        Assert.Equal("[01:02:03] Max: x", new AssistLine(3723.9, "Max", "x").Rendered);

    [Fact]
    public void ContextTextAndRecapPrompt()
    {
        var rec = Record();
        rec.Context.Goal = "Ziel";
        rec.Context.PlaybookItems = new() { new PlaybookItem("P1", "", "x") };
        rec.State.Attendees.Add(new Attendee { Name = "Max Beispiel", Role = "Einkauf", Company = "MUSTER SOLAR" });
        rec.State.Summary.Add("Lager: klein");
        rec.State.OpenPoints.Add(new OpenPoint { Id = "O1", Text = "Budget" });
        rec.State.AskedQuestions.Add(new Question { Text = "Wie groß?" });
        Assert.Equal("""
            CLIENT: MUSTER SOLAR
            PROJECT: P0001 – MUSTER SOLAR rollout
            TEMPLATE: Projekt-Kickoff
            GOAL: Ziel
            USER ("Me" in the transcript): Erika

            ATTENDEES:
            - Max Beispiel, Einkauf, MUSTER SOLAR

            LIVE SUMMARY:
            - Lager: klein

            OPEN POINTS:
            - [open] Budget

            QUESTIONS ASKED:
            - Wie groß?
            """, RecapBuilder.ContextText(rec));
        var fields = new List<AyaField>
        {
            new("summary__value", AyaFieldKind.Markdown, true), new("status__value", AyaFieldKind.Select, true, new[] { "a", "b" }),
        };
        var p = RecapBuilder.RecapPrompt(rec, new[] { "[00:00:01] Me: Hallo" }, fields);
        Assert.EndsWith("""

            TRANSCRIPT:
            [00:00:01] Me: Hallo

            Fill in the meeting recap. Fields:
            - summary__value: Summary — markdown
            - status__value: Status — one of: a, b
            The title should name the client or project and the topic. Attendee fields: names and roles of the external participants. The meeting followed the template "Projekt-Kickoff"; where it fits, structure the summary along its agenda.
            """, p);
    }

    [Fact]
    public void TablesPromptAndSchema()
    {
        var rec = Record();
        rec.Context.TemplateNotes = new() { "Hinweis" };
        var t = TemplateParser.Parse(TemplateMarkdown).Tables;
        var p = RecapBuilder.TablesPrompt(rec, new[] { "[00:00:01] Me: x" }, t);
        Assert.Contains("\n\nTEMPLATE NOTES:\n- Hinweis\n\nTABLE T1 (section Systems)", p);
        Assert.EndsWith("TRANSCRIPT:\n[00:00:01] Me: x\n\nComplete the tables from the whole transcript. Return one entry per cell that the meeting answered: fill empty cells, correct values from the live notes that turned out wrong, leave cells nobody talked about out. Keep values short, in the meeting's language. For a row named \"…:\" (e.g. \"Eigener Vorschlag:\") put the proposal into the name column. Values marked \"(set by user)\" are final; don't return them.", p);
        Assert.Equal("""{"additionalProperties":false,"properties":{"cells":{"items":{"additionalProperties":false,"properties":{"column":{"enum":["Area","Module","Relevant","Used today (system)"]},"evidence":{"description":"short quote from the transcript","type":"string"},"row":{"enum":["T1.1","T1.2","T1.3","T1.4"]},"value":{"type":"string"}},"required":["row","column","value","evidence"],"type":"object"},"type":"array"}},"required":["cells"],"type":"object"}""",
                     JsonIO.Compact(RecapBuilder.TablesSchema(t)));
    }

    [Fact]
    public void CrmSchemaShapes()
    {
        var c = new List<AyaField>();
        var p = new List<AyaField> { new("position__value", AyaFieldKind.Text, true) };
        var s = RecapBuilder.CrmSchema(c, p, p, new[] { "k1" });
        Assert.Equal("""{"maxItems":0,"type":"array"}""", JsonIO.Compact(s["properties"]!["client_changes"]));
        Assert.Equal("""["client_changes","contact_changes","new_contacts"]""", JsonIO.Compact(s["required"]));
        Assert.Equal("""{"enum":["k1"]}""", JsonIO.Compact(s["properties"]!["contact_changes"]!["items"]!["properties"]!["contact_id"]));
        Assert.Equal("""["client_changes"]""", JsonIO.Compact(RecapBuilder.CrmSchema(c, p, new List<AyaField>(), Array.Empty<string>())["required"]));
    }
}
