using System.Globalization;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Scribe;
using MeetingAssistant.Core.Storage;
using Xunit;

namespace MeetingAssistant.Core.Tests.Scribe;

/// Rules of the Scribe screens (spec 01 "Scribe window", "Recording lifecycle", "Pauses", "Editing",
/// "Speakers"; 04 §6.4) and the floating window's paragraphs (01 "Floating live transcript window").
public class ScribeRulesTests
{
    static SessionInfo S(string id, string title, SessionStatus st = SessionStatus.Done) =>
        new() { Id = id, Title = title, Status = st, StartedAt = new DateTime(2026, 11, 10, 13, 3, 0, DateTimeKind.Utc) };

    static Turn T(int id, string speaker, double a, double b, string text = "hallo zusammen und so weiter") =>
        new() { Id = id, Speaker = speaker, Start = a, End = b, Text = text };

    static LiveLine L(string id, double a, double b, string text, string? speaker, bool final = true) =>
        new(id, Track.Mic, a, b, text, speaker, final);

    [Fact]
    public void VisibleSessionsHideRecordingAndFilterCaseInsensitive()
    {
        var all = new[] { S("a", "Weekly MUSTER SOLAR"), S("b", "Beispilop kickoff"), S("c", "Weekly sync", SessionStatus.Recording) };
        Assert.Equal(new[] { "a", "b" }, ScribeRules.VisibleSessions(all, "c", null).Select(s => s.Id));
        Assert.Equal(new[] { "a" }, ScribeRules.VisibleSessions(all, "c", "  weekly ").Select(s => s.Id));
        Assert.Equal("No recordings yet.", ScribeRules.EmptyListText(""));
        Assert.Equal("Nothing matches.", ScribeRules.EmptyListText("x"));
    }

    [Fact]
    public void MoveSelectionClamps()
    {
        Assert.Equal(-1, ScribeRules.MoveSelection(0, -1, 1));
        Assert.Equal(0, ScribeRules.MoveSelection(3, -1, 1));
        Assert.Equal(2, ScribeRules.MoveSelection(3, -1, -1));
        Assert.Equal(2, ScribeRules.MoveSelection(3, 2, 1));
        Assert.Equal(0, ScribeRules.MoveSelection(3, 0, -1));
        Assert.Equal(1, ScribeRules.MoveSelection(3, 0, 1));
    }

    [Fact]
    public void DeleteTexts()
    {
        Assert.Equal("Delete “Weekly”?", ScribeRules.DeleteHeading("Weekly"));
        Assert.Equal("The recording, the transcript and the notes on this PC are deleted.", ScribeRules.DeleteMessage(false));
        Assert.EndsWith(" What was saved in Aya stays there.", ScribeRules.DeleteMessage(true));
    }

    [Fact]
    public void ParticipantsAndHints()
    {
        var o = ScribeRules.ParticipantOptions;
        Assert.Equal(10, o.Count);
        Assert.Equal((0, "Detect automatically"), o[0]);
        Assert.Equal((2, "2 people"), o[1]);
        Assert.Equal((10, "10 people"), o[^1]);
        Assert.StartsWith("Use headphones", ScribeRules.ModeHint(MeetingMode.Call, false, "Erika"));
        Assert.StartsWith("Tip: record a voice sample in Settings so MeetingAssistant can label you as “Erika”.", ScribeRules.ModeHint(MeetingMode.InPerson, false, "Erika"));
        Assert.StartsWith("Your voice sample is set up, so you will be labelled “Erika”.", ScribeRules.ModeHint(MeetingMode.InPerson, true, "Erika"));
        Assert.StartsWith("Remote and in-room speakers", ScribeRules.ModeHint(MeetingMode.Hybrid, true, "Erika"));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.49, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(12.99, 12.5)]
    public void ElapsedIsPublishedInHalfSeconds(double e, double expected) => Assert.Equal(expected, ScribeRules.PublishedElapsed(e));

    [Fact]
    public void StopEarlierLastSpeechAndQuickButtons()
    {
        Assert.Null(ScribeRules.LastSpeech(Array.Empty<LiveLine>(), 100));
        Assert.Equal(52, ScribeRules.LastSpeech(new[] { L("m-0.0", 40, 50, "x", "me") }, 100));
        Assert.Equal(100, ScribeRules.LastSpeech(new[] { L("m-0.0", 90, 99.5, "x", "me") }, 100));
        Assert.Empty(ScribeRules.StopEarlierOptions(300));
        var opts = ScribeRules.StopEarlierOptions(20 * 60);
        Assert.Equal(new[] { "−5 min", "−15 min" }, opts.Select(o => o.Title));
        Assert.Equal(15 * 60, opts[0].KeepUntil);
        Assert.Equal(4, ScribeRules.StopEarlierOptions(61 * 60).Count);
        Assert.True(ScribeRules.ShouldTrimAfterStop(99, 100));
        Assert.False(ScribeRules.ShouldTrimAfterStop(99.6, 100));
    }

    [Theory]
    [InlineData("45", 45.0)]
    [InlineData("62:03", 3723.0)]
    [InlineData("1:02:03", 3723.0)]
    [InlineData(" 1 : 30 ", 90.0)]
    public void ParseTime(string s, double expected) => Assert.Equal(expected, ScribeRules.ParseTime(s));

    [Theory]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("1:2:3:4")]
    public void ParseTimeRejects(string s) => Assert.Null(ScribeRules.ParseTime(s));

    [Fact]
    public void TrimRules()
    {
        Assert.False(ScribeRules.CanTrim(10, 10.5, 100));         // < 1 s kept
        Assert.False(ScribeRules.CanTrim(0.4, 99.6, 100));        // nothing changes
        Assert.True(ScribeRules.CanTrim(0.6, 99.6, 100));
        Assert.True(ScribeRules.CanTrim(0, 99, 100));
        var t = new Transcript { Turns = { T(0, "a", 0, 10), T(1, "b", 20, 97.5) } };
        Assert.Equal(99.5, ScribeRules.TrimLastSpeech(t, 100));
        Assert.Equal(100, ScribeRules.TrimLastSpeech(new Transcript { Turns = { T(0, "a", 0, 99) } }, 100));
        Assert.Null(ScribeRules.TrimLastSpeech(null, 100));
    }

    [Fact]
    public void SplitRules()
    {
        Assert.False(ScribeRules.CanSplit(0.9, 100));
        Assert.False(ScribeRules.CanSplit(99.5, 100));
        Assert.True(ScribeRules.CanSplit(50, 100));
        var p = new Pause(100, 400, PauseKind.Inner);          // split point 398, keep until 102
        Assert.Equal(398, ScribeRules.InitialSplit(p, 5, 600));
        Assert.Equal(5, ScribeRules.InitialSplit(null, 5, 600));
        Assert.Equal(300, ScribeRules.InitialSplit(null, 1, 600));
        Assert.Equal(p, ScribeRules.SplitAtPause(p, 398.4));
        Assert.Null(ScribeRules.SplitAtPause(p, 397.4));
        Assert.True(ScribeRules.CanRemovePause(p));
        Assert.Equal(102, ScribeRules.SplitPart1(p, true, 398));
        Assert.Equal(398, ScribeRules.SplitPart1(p, false, 398));
        Assert.Equal(398, ScribeRules.SplitPart1(null, true, 398));
    }

    [Fact]
    public void TalkShareSearchAndCurrentTurn()
    {
        var t = new Transcript
        {
            Turns = { T(0, "a", 0, 30, "Guten Morgen"), T(1, "b", 30, 40, "Hallo MUSTER SOLAR"), T(2, "a", 40, 50, "Weiter") },
            Speakers = { new Speaker { Id = "a", Label = "Erika Musterfrau" }, new Speaker { Id = "b", Label = "Speaker 2" } },
        };
        Assert.Equal("80%", ScribeRules.TalkShare(t, "a"));
        Assert.Equal("20%", ScribeRules.TalkShare(t, "b"));
        Assert.Equal("", ScribeRules.TalkShare(new Transcript(), "a"));
        Assert.Equal(new[] { 1 }, ScribeRules.FilterTurns(t, "muster solar").Select(x => x.Id));
        Assert.Equal(new[] { 0, 2 }, ScribeRules.FilterTurns(t, "musterfrau").Select(x => x.Id));
        Assert.Equal(3, ScribeRules.FilterTurns(t, "  ").Count);
        Assert.Equal(1, ScribeRules.CurrentTurn(t.Turns, 29.96));
        Assert.Null(ScribeRules.CurrentTurn(new[] { T(0, "a", 5, 6) }, 1));
    }

    [Fact]
    public void RowsInsertPausesWhereTheyBegin()
    {
        var turns = new[] { T(0, "a", 0, 10), T(1, "a", 100, 110) };
        var pauses = new[] { new Pause(10, 100, PauseKind.Inner), new Pause(110, 200, PauseKind.Trailing) };
        var rows = ScribeRules.Rows(turns, pauses, false);
        Assert.Equal(4, rows.Count);
        Assert.IsType<Turn>(rows[0]);
        Assert.IsType<Pause>(rows[1]);
        Assert.IsType<Turn>(rows[2]);
        Assert.IsType<Pause>(rows[3]);
        Assert.Equal(2, ScribeRules.Rows(turns, pauses, true).Count);
    }

    [Fact]
    public void PauseTexts()
    {
        Assert.Equal("Pause · 90 s", ScribeRules.PauseText(new Pause(10, 100, PauseKind.Inner)));
        Assert.Equal("Quiet for the first 4 min", ScribeRules.PauseText(new Pause(0, 240, PauseKind.Leading)));
        Assert.Equal("Quiet for the last 2 min", ScribeRules.PauseText(new Pause(0, 120, PauseKind.Trailing)));
        Assert.Equal("Split Here…", ScribeRules.PauseAction(new Pause(0, 1, PauseKind.Inner)));
        Assert.Equal("Trim Start…", ScribeRules.PauseAction(new Pause(0, 1, PauseKind.Leading)));
        Assert.Equal("Trim End…", ScribeRules.PauseAction(new Pause(0, 1, PauseKind.Trailing)));
        Assert.Equal("No conversation for 4 min, from 12:00 to 16:00.", ScribeRules.SplitHintText(new Pause(720, 960, PauseKind.Inner)));
    }

    [Fact]
    public void SplitHintIsTheLongestUnlessDismissed()
    {
        var pauses = new[] { new Pause(10, 200, PauseKind.Inner), new Pause(300, 700, PauseKind.Inner), new Pause(800, 870, PauseKind.Inner) };
        Assert.Equal(300, ScribeRules.SplitHint(pauses, "s1", new HashSet<string>())!.Value.Start);
        Assert.Null(ScribeRules.SplitHint(pauses, "s1", new HashSet<string> { "s1" }));
        Assert.Null(ScribeRules.SplitHint(new[] { new Pause(10, 100, PauseKind.Inner) }, "s1", new HashSet<string>()));
    }

    [Fact]
    public void TimelineBlocksMergeSmallGaps()
    {
        var turns = new[] { T(0, "a", 0, 5), T(1, "a", 6.5, 9), T(2, "b", 20, 25) };
        Assert.Equal(new[] { (0.0, 9.0), (20.0, 25.0) }, ScribeRules.TimelineBlocks(turns, 600));
        // Long recordings merge gaps up to duration/600 (6 s at 1 hour).
        Assert.Single(ScribeRules.TimelineBlocks(new[] { T(0, "a", 0, 5), T(1, "a", 10.5, 12) }, 3600));
    }

    [Fact]
    public void SpeakerTexts()
    {
        Assert.Equal("", ScribeRules.RenamePrefill("Speaker 3"));
        Assert.Equal("Max Beispiel", ScribeRules.RenamePrefill("Max Beispiel"));
        Assert.Matches("^manual-[0-9A-F]{6}$", ScribeRules.NewManualSpeakerId());
        Assert.Equal("· 3 speakers", ScribeRules.SpeakersText(3));
        Assert.Equal("Not transcribed yet.", ScribeRules.EmptyStateText(SessionStatus.Recorded));
        Assert.Equal("Waiting to be transcribed…", ScribeRules.EmptyStateText(SessionStatus.Queued));
        Assert.Equal("Transcription failed.", ScribeRules.EmptyStateText(SessionStatus.Failed));
    }

    [Fact]
    public void EnrollmentAndLibraryTexts()
    {
        Assert.Equal("Keep reading… (15 s)", ScribeRules.EnrollmentButton(0));
        Assert.Equal("Keep reading… (1 s)", ScribeRules.EnrollmentButton(14.2));
        Assert.Equal("Finish", ScribeRules.EnrollmentButton(15));
        Assert.Equal("from 1 meeting", ScribeRules.LibraryCount(1));
        Assert.Equal("from 3 meetings", ScribeRules.LibraryCount(3));
        var v = new Voiceprint { Seconds = 12.4, CreatedAt = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc) };
        Assert.Equal("12 s recorded 10 Nov 2026", ScribeRules.VoiceprintText(v, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SpeakerPaletteIsFnv1a()
    {
        // The Mac's offset basis is 1469598103934665603 (one digit shorter than the standard FNV
        // basis); kept so colours match the Mac. "a" → 4953267810257967366 → palette index 6.
        Assert.Equal(1469598103934665603UL, SpeakerPalette.Fnv1a(""));
        Assert.Equal(4953267810257967366UL, SpeakerPalette.Fnv1a("a"));
        Assert.Equal(6, SpeakerPalette.Index("a"));
        Assert.Null(SpeakerPalette.Color("x", isMe: true));
        Assert.Equal(10, SpeakerPalette.Colors.Count);
        Assert.Equal(SpeakerPalette.Color("system-2", false), SpeakerPalette.Color("system-2", false));
    }
}

public class FloatingTranscriptTests
{
    static readonly DateTime Start = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Local);

    static LiveLine L(string id, double a, double b, string text, string? speaker, bool final = true, Track track = Track.Mic) =>
        new(id, track, a, b, text, speaker, final);

    [Fact]
    public void JoinsLinesOfOneSpeakerWithin120Seconds()
    {
        var seg = new TranscriptSegment(Start, MeetingMode.Call, false)
        {
            Lines = new[]
            {
                L("mic-0.0", 0, 2, "Hallo", "me"),
                L("mic-1.0", 3, 5, "zusammen", "me"),
                L("system-0.0", 6, 8, "Hi", "system-1", track: Track.System),
                L("system-1.0", 9, 10, "und", "system-1", false, Track.System),
                L("mic-2.0", 200, 201, "später", "me"),
            },
        };
        var p = FloatingTranscript.Build(new[] { seg }, "Erika", DateTime.MinValue);
        Assert.Equal(3, p.Count);
        Assert.Equal("Erika", p[0].Speaker);
        Assert.True(p[0].IsMe);
        Assert.Equal("Hallo zusammen", p[0].FinalText);
        Assert.Equal("Remote 1", p[1].Speaker);
        Assert.Equal("Hi", p[1].FinalText);
        Assert.Equal("und", p[1].PendingText);
        Assert.Equal("Erika", p[2].Speaker);     // 200 s later: new paragraph
        Assert.Equal(Start.AddSeconds(200), p[2].Date);
    }

    [Fact]
    public void FinalAfterPendingGoesToPending()
    {
        var seg = new TranscriptSegment(Start, MeetingMode.InPerson, true)
        {
            Lines = new[] { L("a", 0, 1, "eins", "mic-0", false), L("b", 1, 2, "zwei", "mic-0") },
        };
        var p = FloatingTranscript.Build(new[] { seg }, "", DateTime.MinValue).Single();
        Assert.Equal("Speaker 1", p.Speaker);
        Assert.Equal("", p.FinalText);
        Assert.Equal("eins zwei", p.PendingText);
        Assert.True(p.Recorded);
    }

    [Fact]
    public void SegmentsNumberSpeakersSeparatelyAndClearHides()
    {
        var a = new TranscriptSegment(Start, MeetingMode.InPerson, false) { Lines = new[] { L("a", 0, 1, "eins", "mic-3") } };
        var b = new TranscriptSegment(Start.AddMinutes(10), MeetingMode.InPerson, false) { Lines = new[] { L("b", 0, 1, "zwei", "mic-3"), L("c", 2, 3, "drei", null) } };
        var p = FloatingTranscript.Build(new[] { a, b }, "", DateTime.MinValue);
        Assert.Equal(3, p.Count);
        Assert.NotEqual(p[0].ColorKey, p[1].ColorKey);   // same speaker id, other segment: other paragraph
        Assert.Equal("Speaker 1", p[1].Speaker);
        Assert.Equal("…", p[2].Speaker);
        var cleared = FloatingTranscript.Build(new[] { a, b }, "", Start.AddMinutes(5));
        Assert.Equal(2, cleared.Count);
    }

    [Fact]
    public void PlainText()
    {
        var p = new PanelParagraph("x", new DateTime(2026, 11, 10, 9, 5, 7), "Erika", "0-me", true, "Hallo", "", false);
        var q = p with { Speaker = "Remote 1", FinalText = "", PendingText = "und" };
        Assert.Equal("[09:05:07] Erika: Hallo\n[09:05:07] Remote 1:  und", FloatingTranscript.PlainText(new[] { p, q }));
    }

    [Fact]
    public void RotatesAfterTenMinutesInAPauseOrAfterTwelve()
    {
        var seg = new TranscriptSegment(Start, MeetingMode.Call, false) { Lines = new[] { L("a", 598, 599, "x", "me") } };
        Assert.False(FloatingTranscript.ShouldRotate(seg, Start.AddSeconds(590)));
        Assert.True(FloatingTranscript.ShouldRotate(seg, Start.AddSeconds(605)));             // last line ended 6 s ago
        seg.Lines = new[] { L("a", 600, 603, "x", "me") };
        Assert.False(FloatingTranscript.ShouldRotate(seg, Start.AddSeconds(605)));            // still talking
        seg.Lines = new[] { L("a", 718, 721, "x", "me") };
        Assert.True(FloatingTranscript.ShouldRotate(seg, Start.AddSeconds(721)));             // > 12 min anyway
    }

    [Fact]
    public void TrimDropsOldestFinishedSegmentsAbove4000Lines()
    {
        LiveLine[] Lines(int n) => Enumerable.Range(0, n).Select(i => L($"l{i}", i, i + 1, "x", "me")).ToArray();
        var a = new TranscriptSegment(Start, MeetingMode.Call, false) { Lines = Lines(2500), IsActive = false };
        var b = new TranscriptSegment(Start, MeetingMode.Call, false) { Lines = Lines(1000), IsActive = false };
        var c = new TranscriptSegment(Start, MeetingMode.Call, false) { Lines = Lines(1000) };
        var list = new List<TranscriptSegment> { a, b, c };
        FloatingTranscript.Trim(list, c);
        Assert.Equal(new[] { b, c }, list);
        // The current or a running segment is never dropped.
        var big = new TranscriptSegment(Start, MeetingMode.Call, false) { Lines = Lines(5000) };
        var only = new List<TranscriptSegment> { big };
        FloatingTranscript.Trim(only, big);
        Assert.Single(only);
    }
}
