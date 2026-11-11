using System.Globalization;
using System.Text.Json.Nodes;
using MeetingAssistant.Core.Audio;
using MeetingAssistant.Core.Capture;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;
using Xunit;

namespace MeetingAssistant.Core.Tests.Capture;

/// These tests point AppPaths at a temporary folder, so they must not run in parallel with others.
[CollectionDefinition("CaptureAppPaths", DisableParallelization = true)]
public sealed class CaptureAppPathsCollection { }

[Collection("CaptureAppPaths")]
public sealed class SessionRecorderTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("ma-rec-").FullName;
    double _now = 1000;
    static readonly DateTime Start = new(2026, 11, 10, 14, 3, 22, DateTimeKind.Local);

    public SessionRecorderTests() => AppPaths.OverrideRoot(_root);
    public void Dispose()
    {
        AppPaths.OverrideRoot(null);
        try { Directory.Delete(_root, true); } catch { }
    }

    SessionRecorder New(MeetingMode mode, string? title = null, int? speakers = null, string? language = null) =>
        new(mode, title, speakers, language, clock: () => _now, startedAt: Start);

    static float[] Tone(double seconds, float v = 0.5f) => Enumerable.Repeat(v, (int)(seconds * 16000)).ToArray();

    [Fact]
    public void CreatesTheSessionFolderAndInfo()
    {
        var rec = New(MeetingMode.Call, "  Weekly  ", 3, "german");
        Assert.Equal("2026-11-10_14-03-22", rec.Info.Id);
        Assert.True(Directory.Exists(AppPaths.Session(rec.Info.Id)));
        Assert.Equal("Weekly", rec.Info.Title);
        Assert.Equal(new[] { Track.Mic, Track.System }, rec.Info.Tracks);
        Assert.Equal(3, rec.Info.ExpectedSpeakers);
        Assert.Equal("german", rec.Info.Language);
        Assert.Equal(SessionStatus.Recording, rec.Info.Status);
        Assert.Equal(0, rec.Info.DurationSec);
    }

    [Fact]
    public void SecondRecordingInTheSameSecondGetsASuffix()
    {
        var a = New(MeetingMode.InPerson);
        var b = New(MeetingMode.InPerson);
        var c = New(MeetingMode.InPerson);
        Assert.Equal("2026-11-10_14-03-22", a.Info.Id);
        Assert.Equal("2026-11-10_14-03-22_2", b.Info.Id);
        Assert.Equal("2026-11-10_14-03-22_3", c.Info.Id);
        Assert.Equal(new[] { Track.Mic }, a.Info.Tracks);
    }

    [Fact]
    public void DefaultTitleUsesDateAndTime()
    {
        Assert.Equal("Meeting 10 Nov 2026 at 14:03", SessionRecorder.DefaultTitle(Start, CultureInfo.GetCultureInfo("en-GB")));
        var rec = New(MeetingMode.Call, "   ");
        Assert.StartsWith("Meeting ", rec.Info.Title);
    }

    [Fact]
    public void AutoLanguageIsOmitted()
    {
        var rec = New(MeetingMode.InPerson, language: "auto");
        Assert.Null(rec.Info.Language);
        rec.Start(new ManualAudioSource());
        var json = JsonNode.Parse(File.ReadAllText(AppPaths.Info(rec.Info.Id)))!.AsObject();
        Assert.False(json.ContainsKey("language"));
        Assert.False(json.ContainsKey("expectedSpeakers"));
    }

    [Fact]
    public void LifecycleWritesRecordingThenRecordedWithAlignedTracks()
    {
        var rec = New(MeetingMode.Call);
        var mic = new ManualAudioSource();
        var sys = new ManualAudioSource();
        var liveMic = new List<float>();
        var liveSys = new List<float>();
        rec.OnMicSamples = s => { lock (liveMic) liveMic.AddRange(s); };
        rec.Start(mic, sys);
        rec.OnSystemSamples = s => { lock (liveSys) liveSys.AddRange(s); };   // may be set after start
        Assert.True(mic.IsRunning && sys.IsRunning);

        var onDisk = JsonIO.Read<SessionInfo>(AppPaths.Info(rec.Info.Id));
        Assert.Equal(SessionStatus.Recording, onDisk.Status);
        Assert.Equal(0, onDisk.DurationSec);

        // Mic from the start for 3 s; call audio starts 1 s late and has a 0.5 s dropout.
        mic.Push(Tone(3), 1000);
        sys.Push(Tone(1), 1001);
        sys.Push(Tone(0.25), 1002.5);
        Assert.True(rec.MicLevel > 0.4f);
        _now = 1003;
        Assert.Equal(3, rec.Elapsed, 6);
        Assert.Equal(0f, rec.MicLevel);                            // nothing for > 0.3 s

        var info = rec.Stop();
        Assert.False(mic.IsRunning || sys.IsRunning);
        Assert.Equal(SessionStatus.Recorded, info.Status);
        Assert.Equal(3.0, info.DurationSec, 6);
        var saved = JsonIO.Read<SessionInfo>(AppPaths.Info(rec.Info.Id));
        Assert.Equal(SessionStatus.Recorded, saved.Status);
        Assert.Equal(3.0, saved.DurationSec, 6);

        var m = AudioFile.Read(AppPaths.NewTrack(rec.Info.Id, Track.Mic));
        var s = AudioFile.Read(AppPaths.NewTrack(rec.Info.Id, Track.System));
        Assert.Equal(48000, m.Length);
        Assert.Equal(48000, s.Length);                              // padded to the longer track
        Assert.Equal(0f, s[8000]);                                  // before the late start
        Assert.Equal(0.5f, s[16000 + 100], 3);                      // the call audio at 1.0 s
        Assert.Equal(0f, s[16000 * 2 + 4000]);                      // dropout 2.0–2.5 s
        Assert.Equal(0.5f, s[16000 * 2 + 8000 + 100], 3);           // resumes at 2.5 s
        Assert.Equal(0f, s[^1]);                                    // end padding (call audio ended at 2.75 s)
        Assert.Equal(48000, liveMic.Count);
        Assert.Equal(48000, liveSys.Count);                         // live gets padding too
    }

    [Fact]
    public void SessionJsonMatchesTheMacFormat()
    {
        var rec = New(MeetingMode.Hybrid, "Beispilop Kick-off", 4);
        rec.Start(new ManualAudioSource(), new ManualAudioSource());
        var info = rec.Stop();
        var text = File.ReadAllText(AppPaths.Info(rec.Info.Id));
        var utc = Start.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var expected = "{\n  \"durationSec\" : 0,\n  \"expectedSpeakers\" : 4,\n  \"id\" : \"2026-11-10_14-03-22\",\n  \"mode\" : \"hybrid\",\n" +
                       $"  \"startedAt\" : \"{utc}\",\n  \"status\" : \"recorded\",\n  \"title\" : \"Beispilop Kick-off\",\n" +
                       "  \"tracks\" : [\n    \"mic\",\n    \"system\"\n  ]\n}";
        Assert.Equal(expected, text.TrimEnd('\n'));
        Assert.Equal(0, info.DurationSec);
    }

    [Fact]
    public void InPersonRecordsTheMicOnly()
    {
        var rec = New(MeetingMode.InPerson);
        var mic = new ManualAudioSource();
        var sys = new ManualAudioSource();
        rec.Start(mic, sys);
        Assert.False(sys.IsRunning);
        mic.Push(Tone(1), 1000);
        rec.Stop();
        Assert.True(File.Exists(AppPaths.NewTrack(rec.Info.Id, Track.Mic)));
        Assert.False(File.Exists(AppPaths.NewTrack(rec.Info.Id, Track.System)));
    }

    [Fact]
    public void CallModeNeedsASystemSource()
    {
        var rec = New(MeetingMode.Call);
        Assert.Throws<ArgumentException>(() => rec.Start(new ManualAudioSource()));
    }

    [Fact]
    public void FailingCallAudioStopsTheMicAndRethrows()
    {
        var rec = New(MeetingMode.Call);
        var mic = new ManualAudioSource();
        var sys = new ManualAudioSource { FailOnStart = new InvalidOperationException("no loopback") };
        var e = Assert.Throws<InvalidOperationException>(() => rec.Start(mic, sys));
        Assert.Equal("no loopback", e.Message);
        Assert.False(mic.IsRunning);
        Assert.Equal(1, mic.StopCount);
    }

    [Fact]
    public void SourceFailureIsReportedAndRecordingContinues()
    {
        var rec = New(MeetingMode.Call);
        var mic = new ManualAudioSource();
        var sys = new ManualAudioSource();
        var failures = new List<(Track, string)>();
        rec.SourceFailed += (t, m) => failures.Add((t, m));
        rec.Start(mic, sys);
        mic.Push(Tone(1), 1000);
        sys.Fail("device removed");
        mic.Push(Tone(1), 1001);
        var info = rec.Stop();
        Assert.Equal(new[] { (Track.System, "device removed") }, failures);
        Assert.Equal(2.0, info.DurationSec, 6);
        Assert.Equal(32000, AudioFile.Read(AppPaths.NewTrack(rec.Info.Id, Track.System)).Length);
    }

    [Fact]
    public void UntimedSourcesAreTakenToEndNow()
    {
        var rec = New(MeetingMode.InPerson);
        var src = new UntimedSource();
        rec.Start(src);
        _now = 1002;                                                 // 2 s after the start, a 1 s buffer arrives
        src.Push(Tone(1));
        var info = rec.Stop();
        Assert.Equal(2.0, info.DurationSec, 6);
    }

    [Fact]
    public void FileSourceRecordsAFileInRealTimeOnAScaledClock()
    {
        var clock = new ScaledClock(40);
        var rec = new SessionRecorder(MeetingMode.InPerson, clock: clock.Now, startedAt: Start);
        var tone = Tone(2, 0.3f);
        var src = new FileAudioSource(tone, clock.Now, startDelay: 0.5);
        var done = new ManualResetEventSlim();
        src.Finished += done.Set;
        rec.Start(src);
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        var info = rec.Stop();
        var m = AudioFile.Read(AppPaths.NewTrack(rec.Info.Id, Track.Mic));
        Assert.Equal(m.Length / 16000.0, info.DurationSec, 6);
        Assert.InRange(info.DurationSec, 2.4, 2.7);                 // 0.5 s delay padded + 2 s
        Assert.Equal(0f, m[1000]);
        Assert.Equal(0.3f, m[^100], 3);
    }

    sealed class UntimedSource : MeetingAssistant.Core.Platform.IAudioSource
    {
        public event Action<float[]>? Samples;
        public event Action<string>? Failed { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Push(float[] s) => Samples?.Invoke(s);
    }
}

[Collection("CaptureAppPaths")]
public sealed class LiveCaptureTests
{
    [Fact]
    public void SegmentsRestartTimeAtZero()
    {
        double now = 10;
        var live = new LiveCapture(() => now);
        var mic = new ManualAudioSource();
        var sys = new ManualAudioSource();
        live.Start(mic, sys);
        var a = new List<float>();
        var b = new List<float>();
        live.BeginSegment(s => a.AddRange(s), _ => { });
        mic.Push(new float[1600], 10.5);                          // 0.5 s after the segment start
        Assert.Equal(8000 + 1600, a.Count);
        now = 20;
        live.BeginSegment(s => b.AddRange(s), null);
        mic.Push(new float[1600], 20);
        Assert.Equal(1600, b.Count);
        Assert.Equal(8000 + 1600, a.Count);
        live.Stop();
        Assert.False(mic.IsRunning || sys.IsRunning);
    }
}
