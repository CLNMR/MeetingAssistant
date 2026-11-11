using System.Globalization;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Model;
using Xunit;

namespace MeetingAssistant.Core.Tests.Assist;

/// Display rules of the Assist screens (spec 03 §3, §6, §8, §9, §11).
public class AssistUiTests
{
    // MARK: Live layout (spec 03 §6.4, §6.6)

    [Fact]
    public void SharesWithoutChecklistFillTheRow()
    {
        var (s, o) = LiveLayout.Shares(0.40, 0.30, hasChecklist: false);
        Assert.Equal(0.4 / 0.7, s, 6);
        Assert.Equal(0.3 / 0.7, o, 6);
    }

    [Fact]
    public void SharesWithChecklistKeepFifteenPercent()
    {
        Assert.Equal((0.4, 0.3), LiveLayout.Shares(0.4, 0.3, true));
        var (s, o) = LiveLayout.Shares(0.6, 0.4, true);
        Assert.Equal(0.85, s + o, 6);
        Assert.Equal(0.6 * 0.85, s, 6);
        // Tiny stored shares count as 0.05.
        Assert.Equal((0.05, 0.05), LiveLayout.Shares(0, -1, true));
    }

    [Fact]
    public void DragAfterSummaryTradesWithOpenPointsOnly()
    {
        var cur = (0.4, 0.3);
        Assert.Equal(0.5, LiveLayout.DragAfterSummary(cur, 0.4, 0.1).Summary, 6);
        Assert.Equal(0.2, LiveLayout.DragAfterSummary(cur, 0.4, 0.1).Open, 6);
        // Clamped: each keeps 15 %.
        Assert.Equal((0.55, 0.15), Round(LiveLayout.DragAfterSummary(cur, 0.4, 0.5)));
        Assert.Equal((0.15, 0.55), Round(LiveLayout.DragAfterSummary(cur, 0.4, -0.5)));
    }

    [Fact]
    public void DragAfterOpenKeepsChecklistFifteenPercent()
    {
        var cur = (0.4, 0.3);
        Assert.Equal((0.4, 0.35), Round(LiveLayout.DragAfterOpen(cur, 0.3, 0.05)));
        Assert.Equal((0.4, 0.45), Round(LiveLayout.DragAfterOpen(cur, 0.3, 0.4)));
        Assert.Equal((0.4, 0.15), Round(LiveLayout.DragAfterOpen(cur, 0.3, -0.4)));
    }

    static (double, double) Round((double A, double B) x) => (Math.Round(x.A, 6), Math.Round(x.B, 6));

    [Fact]
    public void AvailableWidthAndHeights()
    {
        Assert.Equal(984, LiveLayout.Available(1000, true));
        Assert.Equal(992, LiveLayout.Available(1000, false));
        Assert.Equal(300, LiveLayout.Available(200, true));
        Assert.Equal(90, LiveLayout.TranscriptHeight(10, 900));
        Assert.Equal(240, LiveLayout.TranscriptHeight(240, 900));
        Assert.Equal(540, LiveLayout.TranscriptHeight(800, 900));
        Assert.Equal(90, LiveLayout.TranscriptHeight(240, 300));
        Assert.Equal(160, LiveLayout.PanelHeight(200, 100, 50, true));
        Assert.Equal(392, LiveLayout.PanelHeight(600, 100, 92, true));
        Assert.Equal(492, LiveLayout.PanelHeight(600, 100, 92, false));
    }

    [Fact]
    public void WaveformBars()
    {
        Assert.Equal(14, LiveLayout.WaveShape.Count);
        Assert.Equal(3, LiveLayout.WaveBarHeight(100, 0), 6);
        Assert.Equal(22 * 0.2, LiveLayout.WaveBarHeight(100, 0.2), 6);
        Assert.Equal(22, LiveLayout.WaveBarHeight(100, 1), 6);
        Assert.Equal(22 * 0.35, LiveLayout.WaveBarHeight(35, 1), 6);
    }

    // MARK: Text

    [Fact]
    public void SummaryTopicIsBoldWithinFortyCharacters()
    {
        Assert.Equal(("Lager", "Bestände unbekannt"), AssistUi.SummaryParagraph("**Lager**: Bestände unbekannt"));
        Assert.Equal(("Pricing", "fixed"), AssistUi.SummaryParagraph("## Pricing: fixed"));
        var longTopic = new string('x', 41) + ": rest";
        Assert.Equal((null, longTopic), AssistUi.SummaryParagraph(longTopic));
        Assert.Equal((null, "No label here"), AssistUi.SummaryParagraph("No label here"));
    }

    [Fact]
    public void InitialAvatar()
    {
        Assert.Equal("E", AssistUi.Initial("erika Musterfrau"));
        Assert.Equal("3", AssistUi.Initial("  3M"));
        Assert.Equal("•", AssistUi.Initial("—"));
        Assert.Equal("•", AssistUi.Initial(null));
    }

    [Fact]
    public void SpeakerColourIsStableFnv()
    {
        // The Mac's start value is 1469598103934665603 (one digit shorter than the FNV offset basis); kept for identical colours.
        Assert.Equal(3, AssistUi.SpeakerColorIndex(""));
        Assert.Equal(6, AssistUi.SpeakerColorIndex("a"));
        Assert.InRange(AssistUi.SpeakerColorIndex("system-2"), 0, 9);
        Assert.Equal(AssistUi.SpeakerColorIndex("system-2"), AssistUi.SpeakerColorIndex("system-2"));
    }

    [Fact]
    public void TruncatesQuotesAt110()
    {
        var s = new string('a', 120);
        Assert.Equal(new string('a', 110) + "…", AssistUi.Truncate(s));
        Assert.Equal("short", AssistUi.Truncate("short"));
    }

    [Fact]
    public void RelativeTimes()
    {
        var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("just now", AssistUi.RelativeTime(now.AddSeconds(-20), now));
        Assert.Equal("5 min ago", AssistUi.RelativeTime(now.AddMinutes(-5), now));
        Assert.Equal("3 hr ago", AssistUi.RelativeTime(now.AddHours(-3), now));
        Assert.Equal("yesterday", AssistUi.RelativeTime(now.AddHours(-30), now));
        Assert.Equal("4 days ago", AssistUi.RelativeTime(now.AddDays(-4), now));
    }

    [Fact]
    public void SessionDateText()
    {
        var d = new DateTime(2026, 11, 12, 14, 5, 0, DateTimeKind.Local);
        Assert.Equal("12 Nov, 14:05", AssistUi.SessionDate(d, CultureInfo.InvariantCulture));
    }

    // MARK: Toggles

    [Fact]
    public void OpenPointAndChecklistToggles()
    {
        Assert.Equal(OpenPointStatus.Answered, AssistUi.Toggle(OpenPointStatus.Open));
        Assert.Equal(OpenPointStatus.Answered, AssistUi.Toggle(OpenPointStatus.Parked));
        Assert.Equal(OpenPointStatus.Open, AssistUi.Toggle(OpenPointStatus.Answered));
        Assert.Equal(PlaybookStatus.Covered, AssistUi.Toggle(PlaybookStatus.Missing));
        Assert.Equal(PlaybookStatus.Covered, AssistUi.Toggle(PlaybookStatus.Partial));
        Assert.Equal(PlaybookStatus.Missing, AssistUi.Toggle(PlaybookStatus.Covered));
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("Yes", "true")]
    [InlineData("ja", "true")]
    [InlineData("1", "true")]
    [InlineData("nein", "false")]
    [InlineData("", "false")]
    public void BooleanValues(string v, string expected) => Assert.Equal(expected, AssistUi.NormalizeBool(v));

    // MARK: People

    [Fact]
    public void AttendeesPresentFirst()
    {
        var (p, m) = AssistUi.SplitAttendees(new[]
        {
            new Attendee { Name = "Max Beispiel", Present = false },
            new Attendee { Name = "Erika Musterfrau" },
            new Attendee { Name = "Lena Beispiel", Present = true },
        });
        Assert.Equal(new[] { "Erika Musterfrau", "Lena Beispiel" }, p.Select(a => a.Name));
        Assert.Equal(new[] { "Max Beispiel" }, m.Select(a => a.Name));
    }

    [Fact]
    public void SuggestionsFilterAndLimit()
    {
        var all = new[] { "Erika Musterfrau", "Max Beispiel", "Lena Béispiel", "A", "B", "C", "D", "E", "F", "G" };
        Assert.Equal(8, AssistUi.FilterSuggestions(all, "", "").Count);
        Assert.Equal(new[] { "Max Beispiel", "Lena Béispiel" }, AssistUi.FilterSuggestions(all, "", "beisp"));
        Assert.Equal(new[] { "Lena Béispiel" }, AssistUi.FilterSuggestions(all, "Max Beispiel", "BEISP"));
    }

    [Fact]
    public void SpeakerTooltips()
    {
        Assert.Equal("Who is this? Click to name the speaker.", AssistUi.SpeakerTooltip("Speaker 2"));
        Assert.Equal("Click for details from Aya, or to rename", AssistUi.SpeakerTooltip("Erika Musterfrau"));
    }

    [Fact]
    public void ReviewSpeakersInOrderOfFirstSpeaking()
    {
        var t = new Transcript
        {
            Speakers =
            {
                new Speaker { Id = "me", Label = "Erika", IsMe = true },
                new Speaker { Id = "s2", Label = "Speaker 2" },
                new Speaker { Id = "s3", Label = "Max Beispiel" },
            },
            Turns =
            {
                new Turn { Speaker = "s2", Start = 1, End = 4, Text = "Hallo" },
                new Turn { Speaker = "me", Start = 5, End = 6, Text = "Hi" },
                new Turn { Speaker = "s2", Start = 10, End = 20, Text = "Ein längerer Satz" },
            },
        };
        var rows = AssistUi.ReviewSpeakers(t);
        Assert.Equal(new[] { "s2", "me", "s3" }, rows.Select(r => r.Id));
        Assert.True(rows[0].IsGeneric);
        Assert.Equal("“Ein längerer Satz”", rows[0].Quote);
        Assert.Equal(13, rows[0].TalkSeconds);
        Assert.Equal(-1, rows[1].ColorIndex);
        Assert.Equal("“”", rows[2].Quote);
    }

    [Fact]
    public void SpeakersWaitingTexts()
    {
        Assert.Equal("The transcript is being made (42 %). Speakers can be named when it is ready.",
            AssistUi.SpeakersWaiting(SessionStatus.Transcribing, 0.425));
        Assert.Equal("The transcript is being made. Speakers can be named when it is ready.",
            AssistUi.SpeakersWaiting(SessionStatus.Transcribing, null));
        Assert.Equal("Waiting to be transcribed. Speakers can be named when the transcript is ready.", AssistUi.SpeakersWaiting(SessionStatus.Queued, null));
        Assert.Equal("Transcription failed. Try “Transcribe Again” in Scribe.", AssistUi.SpeakersWaiting(SessionStatus.Failed, null));
        Assert.Equal("No transcript yet.", AssistUi.SpeakersWaiting(SessionStatus.Done, null));
    }

    // MARK: Tables

    static TemplateTable Table(int rows) => new("T1", "Fragen", "", new[] { "Frage", "Antwort" }, new[] { 1 },
        Enumerable.Range(1, rows).Select(i => new TemplateTable.Row($"T1.{i}", new[] { $"F{i}", "" })));

    [Fact]
    public void LongTablesShowOnlyAnsweredRows()
    {
        Assert.False(AssistUi.OnlyAnsweredByDefault(Table(30)));
        Assert.True(AssistUi.OnlyAnsweredByDefault(Table(31)));
    }

    [Fact]
    public void LatestAnswersOfATable()
    {
        var t = Table(5);
        foreach (var i in new[] { 1, 2, 4, 5 }) t.Set($"T1.{i}", "Antwort", $"A{i}");
        var latest = AssistUi.LatestAnswers(t);
        Assert.Equal(new[] { "F2 → A2", "F4 → A4", "F5 → A5" }, latest.Select(x => x.Text));
        Assert.Equal("Fragen", AssistUi.TableName(t));
    }

    // MARK: Settings

    [Fact]
    public void ListChoicesAreLabelledAndSorted()
    {
        var ws = new[] { new AyaWorkspace { Id = "w1", Slug = "Vertrieb" } };
        var specs = new[]
        {
            new AyaRecordSpec { TechName = "kontakt", WorkspaceId = "w1", LocalizedLabel = new() { ["en"] = "Kontakte" } },
            new AyaRecordSpec { TechName = "firma", WorkspaceId = "w1", LocalizedLabel = new() { ["en"] = "Firmen" } },
            new AyaRecordSpec { TechName = "users", WorkspaceId = "w1", IsSystem = true },
        };
        var c = AssistUi.ListChoices(specs, ws);
        Assert.Equal(new[] { "Firmen · Vertrieb", "Kontakte · Vertrieb" }, c.Select(x => x.Title));
    }

    [Fact]
    public void TablesFieldChoices()
    {
        var schema = new AyaListSchema("w", "pmMeetings", new[]
        {
            new AyaField("summary__value", AyaFieldKind.Markdown, true),
            new AyaField("protokoll__value", AyaFieldKind.Markdown, true),
            new AyaField("title__value", AyaFieldKind.Text, true),
        });
        var c = AssistUi.TablesFieldChoices(schema, new AyaConfig { MeetingTablesField = "-" });
        Assert.Equal(new[] { "Automatic (Protokoll)", "Summary", "Protokoll", "Don't save tables" }, c.Select(x => x.Title));
        Assert.Null(c[0].Value);
        Assert.Equal("-", c[^1].Value);
        Assert.Equal("Automatic", AssistUi.TablesFieldChoices(null, new AyaConfig())[0].Title);
    }

    [Fact]
    public void MeetingChoiceText()
    {
        var m = AyaRecord.Of(("id", "m1"), ("computedTitle", "Kick-off"));
        Assert.Equal("Kick-off", AssistUi.MeetingChoice(m, "meeting_date__value"));
    }
}
