using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Platform;
using Xunit;
using static MeetingAssistant.Core.Tests.Assist.AssistTestData;

namespace MeetingAssistant.Core.Tests.Assist;

sealed class FakeHost : IAssistHost
{
    public string MyName { get; set; } = "Erika";
    public bool IsRecording { get; set; }
    public Dictionary<string, SessionInfo> Infos { get; } = new();
    public List<(string Name, float[] E)> Learned { get; } = new();
    public List<string> Deleted { get; } = new();
    public int ShowAssistCalls;
    public Task<(string? SessionId, string? Error)> StartRecordingAsync(MeetingMode mode, string title, Action<IReadOnlyList<LiveLine>> liveLines, Action<string> stopped) =>
        Task.FromResult<(string?, string?)>((null, "no microphone"));
    public Task StopRecordingAsync() => Task.CompletedTask;
    public SessionInfo? Session(string id) => Infos.TryGetValue(id, out var s) ? s : null;
    public double? Progress(string id) => null;
    public IReadOnlyList<SessionInfo> Sessions => Infos.Values.ToList();
    public void ReloadSessions() { }
    public void Enqueue(string id) { }
    public void DeleteSession(string id) => Deleted.Add(id);
    public Task<SessionInfo> ImportFilesAsync(string mic, string? system, MeetingMode mode, string title, DateTime startedAt) => throw new NotSupportedException();
    public void LearnSpeaker(string name, float[] embedding) => Learned.Add((name, embedding));
    public void ShowAssist() => ShowAssistCalls++;
}

sealed class FakeRecapClaude : IRecapClaude
{
    public int Drafts;
    public Task<List<ProposedValue>> DraftRecapAsync(string binary, string model, AssistRecord record, string sessionID, AyaListSchema schema, AyaConfig config, AyaRecord? existing)
    {
        Drafts++;
        return Task.FromResult(RecapBuilder.Target(Fields(), existing));
    }
    public Task<List<TemplateTable>> CompleteTablesAsync(string binary, string model, AssistRecord record, string sessionID, IReadOnlyList<TemplateTable> tables) =>
        Task.FromResult(tables.ToList());
    public Task<CRMProposal> ProposeCrmAsync(string binary, string model, AssistRecord record, string sessionID, AyaRecord client, IReadOnlyList<AyaRecord> contacts, AyaListSchema clientSchema, AyaListSchema? contactSchema) =>
        Task.FromResult(new CRMProposal());
}

[Collection("AppRoot")]
public class AssistSetupModelTests
{
    [Fact]
    public async Task PickingAPlannedMeetingTakesItsLinksAndNewMeetingDropsThem()
    {
        using var home = new TempHome();
        var prefs = new MemoryPreferences();
        var cfg = new AyaConfig { OrgId = "ORG" };
        cfg.SetList(AyaSlot.Meetings, new AyaListRef("W", "WS", "pmMeetings", "Meetings"));
        cfg.Save(prefs);
        var aya = new AssistConnection(new MemorySecretStore(), prefs);
        typeof(AssistConnection).GetProperty(nameof(AssistConnection.Upcoming))!.SetValue(aya, new List<AyaRecord>
        {
            Rec(("id", "M1"), ("customer__id", "C1"), ("project__id", "P1"), ("load_template__id", "T1")),
        });
        var loads = new List<(string?, string?, string?, string?)>();
        var setup = new AssistSetupModel(aya, (m, c, p, t) => { loads.Add((m, c, p, t)); return Task.FromResult(new AyaMeetingContext()); });
        setup.MeetingID = "M1";
        Assert.Equal(("C1", "P1", "T1"), (setup.ClientID, setup.ProjectID, setup.TemplateID));
        Assert.True(setup.PreviewState.IsWorking);
        setup.ProjectID = "P2";                                  // the user changes the project
        setup.MeetingID = null;                                  // back to "New meeting"
        Assert.Equal((null, "P2", null), (setup.ClientID, setup.ProjectID, setup.TemplateID));
        await Task.Delay(AssistSetupModel.PreviewDelay + TimeSpan.FromMilliseconds(200));
        Assert.Equal((null, null, "P2", null), Assert.Single(loads));   // only the last selection was loaded
        Assert.IsType<StepState.Done>(setup.PreviewState);
        setup.ProjectID = null;
        Assert.Null(setup.Preview);
        Assert.IsType<StepState.Idle>(setup.PreviewState);
    }

    [Fact]
    public void CardTexts()
    {
        var now = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Local);
        Assert.StartsWith("Today ", AssistSetupModel.PlannedDateText(now.AddHours(6).ToUniversalTime(), now));
        Assert.StartsWith("Tomorrow ", AssistSetupModel.PlannedDateText(now.AddDays(1).ToUniversalTime(), now));
        Assert.Equal("No date", AssistSetupModel.PlannedDateText(null, now));
        Assert.True(AssistSetupModel.HasNotes(Rec(("next_steps__value", "- x"))));
        Assert.False(AssistSetupModel.HasNotes(Rec(("summary__value", " "))));
    }
}

[Collection("AppRoot")]
public class AssistReviewControllerTests
{
    static (AssistReviewController, FakeHost, FakeRecapClaude, AssistConnection) Make()
    {
        var host = new FakeHost();
        var aya = new AssistConnection(new MemorySecretStore(), new MemoryPreferences());
        var claude = new FakeRecapClaude();
        return (new AssistReviewController(host, aya, claude), host, claude, aya);
    }

    static void Session(FakeHost host, string id, SessionStatus status)
    {
        host.Infos[id] = new SessionInfo { Id = id, Title = "Kickoff", StartedAt = DateTime.UtcNow, Mode = MeetingMode.Call, Status = status };
        JsonIO.Write(host.Infos[id], AppPaths.Info(id));
    }

    [Fact]
    public async Task DraftEditsAreSavedAfterTheDelayAndFlushedOnFinish()
    {
        using var home = new TempHome();
        var (review, host, _, _) = Make();
        Session(host, "S1", SessionStatus.Recording);
        var rec = Record();
        rec.RecapDraft = Fields();
        rec.Save("S1");
        Assert.True(review.Open("S1"));
        Assert.Equal(1, host.ShowAssistCalls);
        Assert.Equal("Kickoff MUSTER SOLAR", review.RecapFields[0].Value);

        review.UpdateField("title__value", f => f.Value = "Neu");
        Assert.Equal("Kickoff MUSTER SOLAR", AssistRecord.Load("S1")!.RecapDraft![0].Value);    // not yet
        await Task.Delay(AssistReviewController.DraftSaveDelay + TimeSpan.FromMilliseconds(300));
        Assert.Equal("Neu", AssistRecord.Load("S1")!.RecapDraft![0].Value);

        review.UpdateField("title__value", f => f.Value = "Sofort");
        review.Finish();                                                                  // flushes immediately
        Assert.Equal("Sofort", AssistRecord.Load("S1")!.RecapDraft![0].Value);
        Assert.Null(review.SessionId);
        Assert.False(review.Open("missing"));
    }

    [Fact]
    public async Task AnEmptyDraftIsSavedAsNull()
    {
        using var home = new TempHome();
        var (review, host, _, _) = Make();
        Session(host, "S1", SessionStatus.Recording);
        var rec = Record();
        rec.RecapDraft = Fields();
        rec.Save("S1");
        review.Open("S1");
        review.RecapFields = new();
        review.FlushDraft();
        Assert.Null(AssistRecord.Load("S1")!.RecapDraft);
        review.Finish();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AfterTheTranscriptNamesAreCarriedOnceAndTheRecapDrafted()
    {
        using var home = new TempHome();
        var (review, host, claude, aya) = Make();
        Session(host, "S2", SessionStatus.Done);
        var rec = Record();
        rec.Lines = new() { new AssistLine(1, "Max Beispiel", "Guten Tag zusammen wie geht es") };
        rec.Save("S2");
        JsonIO.Write(new Transcript
        {
            Turns = { new Turn { Id = 0, Speaker = "a", Start = 0, End = 10, Text = "Guten Tag", Track = Track.System } },
            Speakers = { new Speaker { Id = "a", Label = "Speaker 1", Track = Track.System, Embedding = new[] { 1f, 0f } } },
            Engine = "test",
        }, AppPaths.TranscriptFile("S2"));
        review.Open("S2");
        await Task.Delay(300);
        Assert.Equal("Max Beispiel", review.Transcript?.Speakers[0].Label);
        Assert.True(AssistRecord.Load("S2")!.SpeakersCarried);
        // No meetings schema: the draft fails with the connect hint, Claude is not called.
        Assert.Equal("Connect Aya in Settings › Assist first.", review.RecapState.Text);
        Assert.Equal(0, claude.Drafts);

        // Renaming a generic speaker teaches the library and follows in the draft.
        JsonIO.Write(new Transcript
        {
            Turns = { new Turn { Id = 0, Speaker = "a", Start = 0, End = 10, Text = "x", Track = Track.System } },
            Speakers = { new Speaker { Id = "a", Label = "Speaker 3", Track = Track.System, Embedding = new[] { 1f, 0f } } },
        }, AppPaths.TranscriptFile("S2"));
        review.RecapFields = new() { new ProposedValue { Key = "summary__value", Value = "Speaker 3 und Speaker 31 sprachen" } };
        review.RenameFinalSpeaker("a", "Lena Beispiel");
        Assert.Equal("Lena Beispiel und Speaker 31 sprachen", review.RecapFields[0].Value);
        Assert.Equal("Lena Beispiel", Assert.Single(host.Learned).Name);
        review.Finish();
    }

    [Fact]
    public void ReviewCellsAreUserSetAndSaved()
    {
        using var home = new TempHome();
        var (review, host, _, _) = Make();
        Session(host, "S3", SessionStatus.Recording);
        var rec = Record();
        rec.State.Tables = TemplateParser.Parse(TemplateMarkdown).Tables;
        rec.State.Playbook = new() { new PlaybookItem("P1", "", "Ziel") { Status = PlaybookStatus.Covered }, new PlaybookItem("P2", "", "Budget") };
        rec.Save("S3");
        review.Open("S3");
        review.SetCell("T1.2", "Relevant", "ja");
        var saved = AssistRecord.Load("S3")!;
        Assert.Contains("Relevant", saved.Tables![0].Rows[1].UserSet);
        Assert.Equal("Template: 1 of 2 points covered · missing: Budget", review.ChecklistLine);
        review.Finish();
    }
}

public class AssistControllerRulesTests
{
    [Fact]
    public void BusySessionsCannotBeDeleted()
    {
        Assert.True(AssistController.IsBusy(new SessionInfo { Status = SessionStatus.Queued }));
        Assert.True(AssistController.IsBusy(null));
        Assert.False(AssistController.IsBusy(new SessionInfo { Status = SessionStatus.Done }));
        Assert.Equal("Delete “Kickoff”?", AssistController.DeleteTitle("Kickoff"));
    }

    [Fact]
    public void FrequencyPresets()
    {
        var s = new AssistSettings();
        Assert.Equal(AssistFrequency.Normal, s.Frequency);
        s.Frequency = AssistFrequency.Frequent;
        Assert.Equal((40, 15.0, 45.0), (s.MinWords, s.MinInterval, s.MaxInterval));
        s.Frequency = AssistFrequency.Economical;
        Assert.Equal((100, 40.0, 120.0), (s.MinWords, s.MinInterval, s.MaxInterval));
        s.MinWords = 41;
        Assert.Equal(AssistFrequency.Normal, s.Frequency);
        s.MinWords = 99;
        Assert.Equal(AssistFrequency.Normal, s.Frequency);
        Assert.Equal("About every 15–45 s. Fastest reactions, uses the most.", AssistSettings.FrequencyExplanation(AssistFrequency.Frequent));
        var prefs = new MemoryPreferences();
        s.Save(prefs);
        Assert.Equal(99, AssistSettings.Load(prefs).MinWords);
        Assert.Equal(new AssistSettings(), AssistSettings.Load(new MemoryPreferences()));
        Assert.Contains("\"restartTokens\":80000", prefs.GetString(AssistSettings.PreferencesKey));
    }

    [Fact]
    public void StatusTexts()
    {
        Assert.Equal("Reconnecting… no answer", new AssistStatus.Reconnecting("no answer").Text);
        Assert.Equal("Starting Claude…", new AssistStatus.Starting().Text);
        Assert.Equal("Not started", new AssistStatus.Idle().Text);
        Assert.True(new AssistStatus.Failed("x").IsWarning);
    }

    [Fact]
    public void FilePreferencesKeepOtherKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), "prefs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"other.key":{"x":1}}""");
            var p = new FilePreferences(() => path);
            p.SetString("aya.config.v2", "{}");
            Assert.Equal("{}", p.GetString("aya.config.v2"));
            Assert.Contains("other.key", File.ReadAllText(path));
            p.SetString("aya.config.v2", null);
            Assert.Null(p.GetString("aya.config.v2"));
        }
        finally { File.Delete(path); }
    }
}
