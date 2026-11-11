using System.Text.Json.Nodes;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Model;
using Xunit;

namespace MeetingAssistant.Core.Tests.Assist;

/// Shared invented fixtures (no real names or ids).
static class AssistTestData
{
    public static AssistContext Context() => new()
    {
        ClientID = "c", ClientTitle = "Client", Mode = MeetingMode.Call, MyName = "Erika", Date = DateTime.UtcNow,
    };

    public static AyaRecord Rec(params (string Key, object? Value)[] values)
    {
        var o = new JsonObject();
        foreach (var (k, v) in values)
            o[k] = v switch
            {
                null => null,
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                double d => JsonValue.Create(d),
                int i => JsonValue.Create(i),
                string[] a => new JsonArray(a.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                _ => throw new ArgumentException(),
            };
        return new AyaRecord(o);
    }

    public static AyaListSchema MeetingSchema() => new("W", "pmMeetings", new[]
    {
        new AyaField("title__value", AyaFieldKind.Text, true),
        new AyaField("summary__value", AyaFieldKind.Markdown, true),
        new AyaField("meeting_date__value", AyaFieldKind.Datetime, true),
        new AyaField("external_meeting_id__value", AyaFieldKind.Text, true),
        new AyaField("meeting_type__value", AyaFieldKind.Select, true, new[] { "customer", "internal" }),
        new AyaField("customer__id", AyaFieldKind.Relation, true),
        new AyaField("project__id", AyaFieldKind.Relation, true),
        new AyaField("load_template__id", AyaFieldKind.Relation, true),
        new AyaField("participants__ids", AyaFieldKind.Relations, true),
    });

    public static AssistRecord Record()
    {
        var c = Context();
        c.ClientID = "C1"; c.ClientTitle = "MUSTER SOLAR";
        c.ProjectID = "P1"; c.ProjectTitle = "P0001 – MUSTER SOLAR rollout";
        c.PlaybookID = "T1"; c.PlaybookTitle = "Projekt-Kickoff";
        c.MeetingType = "customer";
        return new AssistRecord(c, new AssistState(), Array.Empty<AssistLine>());
    }

    public static List<ProposedValue> Fields() => new()
    {
        new ProposedValue { Key = "title__value", Label = "Title", Kind = AyaFieldKind.Text, Value = "Kickoff MUSTER SOLAR", Include = true },
        new ProposedValue { Key = "summary__value", Label = "Summary", Kind = AyaFieldKind.Markdown, Value = "- went well", Include = true },
    };

    public const string TemplateMarkdown = """
        ## Agenda
        1. Start (5 Min.)
        2. Systems
        3. Wrap-up


        ## 1. Start (5 Min.)
        - [ ] Introduce the goal

        ## 2. Systems
        Which modules do you use today, and which matter to you?

        | Area | Module | Used today (system) | Relevant |
        |---|---|---|---|
        | Sales | CRM | | |
        | Sales | Quotes | | |
        | Stock | Warehouse | | |
        | Own idea: | | | |

        Per module in use:
        - [ ] Top three features?
        - [ ] Biggest benefit?

        ### Delivery model
        Our idea: standard modules that you adapt yourselves.
        - [ ] Could you adapt them yourselves?

        ## 3. Wrap-up (last minutes)
        - [ ] Next steps explained
        """;

    /// A temporary app data folder for tests that write files.
    public sealed class TempHome : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "ma-assist-" + Guid.NewGuid().ToString("N"));
        public TempHome() { Directory.CreateDirectory(Dir); AppPaths.OverrideRoot(Dir); }
        public void Dispose() { AppPaths.OverrideRoot(null); try { Directory.Delete(Dir, true); } catch { } }
    }
}

/// Tests that change AppPaths.Root must not run in parallel with each other.
[CollectionDefinition("AppRoot", DisableParallelization = true)]
public sealed class AppRootCollection { }
