using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Platform;
using Xunit;
using static MeetingAssistant.Core.Tests.Assist.AssistTestData;

namespace MeetingAssistant.Core.Tests.Assist;

/// A host whose import creates a session folder (or fails).
sealed class ImportHost : IAssistHost
{
    public string MyName { get; set; } = "Erika";
    public bool IsRecording => false;
    public List<(string Mic, string? System, MeetingMode Mode, string Title, DateTime At)> Imports { get; } = new();
    public List<string> Enqueued { get; } = new();
    public bool Fail { get; set; }
    readonly Dictionary<string, SessionInfo> _infos = new();

    public Task<(string? SessionId, string? Error)> StartRecordingAsync(MeetingMode mode, string title, Action<IReadOnlyList<LiveLine>> liveLines, Action<string> stopped) =>
        Task.FromResult<(string?, string?)>((null, "no"));
    public Task StopRecordingAsync() => Task.CompletedTask;
    public SessionInfo? Session(string id) => _infos.TryGetValue(id, out var s) ? s : null;
    public double? Progress(string id) => null;
    public IReadOnlyList<SessionInfo> Sessions => _infos.Values.ToList();
    public void ReloadSessions() { }
    public void Enqueue(string id) => Enqueued.Add(id);
    public void DeleteSession(string id) { }
    public void LearnSpeaker(string name, float[] embedding) { }
    public void ShowAssist() { }

    public Task<SessionInfo> ImportFilesAsync(string mic, string? system, MeetingMode mode, string title, DateTime startedAt)
    {
        if (Fail) throw new IOException("The file could not be read.");
        Imports.Add((mic, system, mode, title, startedAt));
        var id = "import_" + Imports.Count;
        Directory.CreateDirectory(AppPaths.Session(id));
        var info = new SessionInfo { Id = id, Title = title, StartedAt = startedAt.ToUniversalTime(), Mode = mode, Status = SessionStatus.Queued };
        _infos[id] = info;
        return Task.FromResult(info);
    }
}

[Collection("AppRoot")]
public class AssistImportModelTests
{
    static (AssistImportModel, ImportHost, List<DateTime>) Make(Func<DateTime, List<AyaRecord>>? day = null)
    {
        var host = new ImportHost();
        var assist = new AssistController(host, new MemorySecretStore(), new MemoryPreferences(), new FakeRecapClaude());
        var loads = new List<DateTime>();
        var model = new AssistImportModel(assist, d => { loads.Add(d); return Task.FromResult(day?.Invoke(d) ?? new List<AyaRecord>()); },
                                          new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Local));
        return (model, host, loads);
    }

    [Fact]
    public void KeepsAtMostTwoFilesAndTakesTheFirstFilesDate()
    {
        var (m, _, _) = Make();
        Assert.Equal("Choose…", m.ChooseTitle);
        Assert.False(m.CanImport);
        var created = new DateTime(2026, 11, 3, 14, 5, 0, DateTimeKind.Local);
        m.ChooseFiles(new[] { @"C:\a\mic.m4a", @"C:\a\call.m4a", @"C:\a\third.m4a" }, created);
        Assert.Equal(2, m.Files.Count);
        Assert.Equal(created, m.Date);
        Assert.Equal("Change…", m.ChooseTitle);
        Assert.True(m.HasTwoFiles);
        Assert.Equal(MeetingMode.Call, m.Mode);
        m.TwoTrackMode = MeetingMode.Hybrid;
        Assert.Equal(MeetingMode.Hybrid, m.Mode);
        m.ChooseFiles(new[] { "/tmp/one.wav" });
        Assert.Equal(MeetingMode.InPerson, m.Mode);
        Assert.Equal("one.wav", m.FilesText);
        Assert.True(m.CanImport);
    }

    [Fact]
    public async Task ChangingTheDayReloadsAndClearsAMeetingOfAnotherDay()
    {
        var at = new DateTime(2026, 11, 10, 15, 30, 0, DateTimeKind.Local).ToUniversalTime();
        var (m, _, loads) = Make(d => d.Day == 10
            ? new List<AyaRecord> { Rec(("id", "M1"), ("computedTitle", "Kick-off"), ("meeting_date__value", at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))) }
            : new List<AyaRecord>());
        await m.LoadDayAsync();
        Assert.Single(m.DayMeetings);
        m.MeetingID = "M1";
        Assert.False(m.IsNewMeeting);
        Assert.Equal(15, m.Date.Hour);                       // the meeting brings its own time
        Assert.Single(loads);                                // same day: no reload
        m.Date = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Local);
        await m.LoadDayAsync();
        Assert.Equal(2, loads.Count);
        Assert.Null(m.MeetingID);
        Assert.Empty(m.DayMeetings);
    }

    [Fact]
    public async Task ImportsANewMeetingWithTheTypedTitle()
    {
        using var home = new TempHome();
        var (m, host, _) = Make();
        m.ChooseFiles(new[] { "/x/mic.wav", "/x/call.wav" });
        m.TwoTrackMode = MeetingMode.Hybrid;
        m.Title = "Beispilop Workshop";
        Assert.True(await m.ImportAsync());
        var imp = Assert.Single(host.Imports);
        Assert.Equal(("/x/mic.wav", "/x/call.wav", MeetingMode.Hybrid, "Beispilop Workshop"), (imp.Mic, imp.System, imp.Mode, imp.Title));
        Assert.Equal(new[] { "import_1" }, host.Enqueued);
        var rec = AssistRecord.Load("import_1");
        Assert.Equal("Beispilop Workshop", rec!.Context.MeetingTitle);
        Assert.IsType<StepState.Idle>(m.State);
    }

    [Fact]
    public async Task UntitledImportUsesTheFileNameAndShowsErrors()
    {
        using var home = new TempHome();
        var (m, host, _) = Make();
        m.ChooseFiles(new[] { "/x/Aufnahme 3.m4a" });
        Assert.True(await m.ImportAsync());
        Assert.Equal(("Aufnahme 3", MeetingMode.InPerson), (host.Imports[0].Title, host.Imports[0].Mode));
        host.Fail = true;
        Assert.False(await m.ImportAsync());
        Assert.Equal("The file could not be read.", Assert.IsType<StepState.Failed>(m.State).Message);
        Assert.True(m.CanImport);
    }
}
