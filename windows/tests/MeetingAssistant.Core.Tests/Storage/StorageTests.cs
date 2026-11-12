using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MeetingAssistant.Core.Audio;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Storage;
using Xunit;

namespace MeetingAssistant.Core.Tests.Storage;

// Ported from Tests/MeetingAssistantCoreTests/LogicTests.swift (ExporterTests, PauseTests,
// SessionFileTests) plus tests for the Windows-side storage (WAV/CAF editing, preferences, queue).

static class Fixtures
{
    public static Turn TurnWithWords(string text, double a, double b, string speaker = "s1")
    {
        var parts = text.Split(' ');
        var step = (b - a) / parts.Length;
        return new Turn
        {
            Speaker = speaker, Start = a, End = b, Text = text, Track = Track.Mic,
            Words = parts.Select((w, i) => new Word(w, a + i * step, a + (i + 1) * step)).ToList(),
        };
    }

    public static Transcript TranscriptOf(params Turn[] turns) => new() { Turns = turns.ToList(), Engine = "test" };

    /// 16-bit mono PCM CAF like the Mac writes (desc, free padding to 4096, data).
    public static void WriteCafInt16(string path, int frames, double rate = 8000, Func<int, short>? sample = null)
    {
        using var fs = File.Create(path);
        var b = new byte[4096];
        Encoding.ASCII.GetBytes("caff").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), 1);
        Encoding.ASCII.GetBytes("desc").CopyTo(b, 8);
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(12), 32);
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(20), BitConverter.DoubleToInt64Bits(rate));
        Encoding.ASCII.GetBytes("lpcm").CopyTo(b, 28);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(36), 2);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(44), 1);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(48), 16);
        Encoding.ASCII.GetBytes("free").CopyTo(b, 52);
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(56), 4096 - 16 - 64);
        fs.Write(b, 0, 4096 - 16);
        var dh = new byte[16];
        Encoding.ASCII.GetBytes("data").CopyTo(dh, 0);
        BinaryPrimitives.WriteInt64BigEndian(dh.AsSpan(4), frames * 2L + 4);
        fs.Write(dh);
        var data = new byte[frames * 2];
        for (int i = 0; i < frames; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), sample?.Invoke(i) ?? 0);
        fs.Write(data);
    }

    /// 16-bit mono WAV at any rate.
    public static void WriteWav(string path, int frames, int rate = 8000, Func<int, short>? sample = null)
    {
        using var fs = File.Create(path);
        var h = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), 36 + frames * 2);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(h, 8);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(40), frames * 2);
        fs.Write(h);
        var data = new byte[frames * 2];
        for (int i = 0; i < frames; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), sample?.Invoke(i) ?? 0);
        fs.Write(data);
    }
}

public class ExporterTests
{
    [Fact]
    public void FormatsTimes()
    {
        Assert.Equal("01:05", Exporter.Clock(65));
        Assert.Equal("1:02:05", Exporter.Clock(3725));
        Assert.Equal("59:59", Exporter.Clock(3599.9));
        Assert.Equal("01:02:05,500", Exporter.SrtTime(3725.5));
    }

    static (Transcript, SessionInfo) Sample()
    {
        var t = new Transcript
        {
            Turns = { new Turn { Id = 0, Speaker = "me", Start = 0, End = 1, Text = "Hallo", Track = Track.Mic, Words = { new Word("Hallo", 0, 1) } } },
            Speakers = { new Speaker { Id = "me", Label = "Erika", Track = Track.Mic, IsMe = true } },
            Engine = "x", CreatedAt = DateTime.UtcNow, ProcessingSeconds = 1,
        };
        var info = new SessionInfo { Id = "x", Title = "Weekly", StartedAt = new DateTime(2026, 11, 10, 13, 3, 0, DateTimeKind.Utc), DurationSec = 60, Mode = MeetingMode.Call, Tracks = { Track.Mic }, Status = SessionStatus.Done };
        return (t, info);
    }

    [Fact]
    public void RendersMarkdownWithLabels()
    {
        var (t, info) = Sample();
        var md = Exporter.Render(t, info, ExportFormat.Markdown, CultureInfo.GetCultureInfo("en-GB"));
        Assert.Contains("# Weekly", md);
        Assert.Contains("**Erika** [00:00]: Hallo", md);
        Assert.Contains(" · 01:00 · Call*\n\n**Participants:** Erika\n\n", md);
        Assert.EndsWith("Hallo\n\n", md);
    }

    [Fact]
    public void PlainTextAndSrt()
    {
        var (t, info) = Sample();
        Assert.Equal("[00:00] Erika: Hallo\n", Exporter.Render(t, info, ExportFormat.Text));
        Assert.Equal("1\n00:00:00,000 --> 00:00:01,000\nErika: Hallo\n\n", Exporter.Render(t, info, ExportFormat.Srt));
        // 12-word cues; end at least start + 0.5 s.
        var turn = Fixtures.TurnWithWords(string.Join(" ", Enumerable.Range(1, 13).Select(i => $"w{i}")), 10, 10.13, "me");
        t.Turns = new List<Turn> { turn };
        var srt = Exporter.Render(t, info, ExportFormat.Srt);
        Assert.Contains("1\n00:00:10,000 --> 00:00:10,500\nErika: w1 w2 w3 w4 w5 w6 w7 w8 w9 w10 w11 w12\n\n2\n", srt);
        Assert.EndsWith("Erika: w13\n\n", srt);
        t.Turns[0].Words.Clear();
        Assert.Equal("", Exporter.Render(t, info, ExportFormat.Srt));     // no words, no cue
    }

    [Fact]
    public void JsonIsTheTranscriptFile()
    {
        var (t, info) = Sample();
        Assert.Equal(JsonIO.Serialize(t), Exporter.Render(t, info, ExportFormat.Json));
        Assert.Equal("Weekly.srt", Exporter.DefaultFileName(info, ExportFormat.Srt));
        Assert.Equal(ExportFormat.Text, Exporter.Parse("txt"));
    }
}

public class PauseTests
{
    [Fact]
    public void FindsInnerLeadingAndTrailingPauses()
    {
        var t = Fixtures.TranscriptOf(Fixtures.TurnWithWords("hallo zusammen wie geht es", 200, 210),
            Fixtures.TurnWithWords("das war es dann für heute", 110 + 100, 120 + 100),
            Fixtures.TurnWithWords("guten morgen und willkommen hier", 500, 510));
        var p = Pauses.Find(t, 600);
        Assert.Equal(new[] { PauseKind.Leading, PauseKind.Inner, PauseKind.Trailing }, p.Select(x => x.Kind));
        Assert.Equal(220, p[1].Start);
        Assert.Equal(500, p[1].End);
        Assert.True(p[1].SuggestsSplit);
        Assert.Equal(498, p[1].SplitPoint);
        Assert.Equal(222, p[1].KeepUntil);
        Assert.False(p[2].SuggestsSplit);
    }

    [Fact]
    public void StrayWordsDoNotBreakAPause()
    {
        var t = Fixtures.TranscriptOf(Fixtures.TurnWithWords("wir sehen uns nächste woche", 0, 10),
            Fixtures.TurnWithWords("ne", 130, 131),
            Fixtures.TurnWithWords("okay, one moment.", 260, 262),
            Fixtures.TurnWithWords("so jetzt starten wir mal richtig", 500, 510));
        var p = Pauses.Find(t, 512);
        Assert.Single(p);
        Assert.Equal(10, p[0].Start);
        Assert.Equal(500, p[0].End);
    }

    [Fact]
    public void ShortQuietStartIsNotMarked() =>
        Assert.Empty(Pauses.Find(Fixtures.TranscriptOf(Fixtures.TurnWithWords("so dann fangen wir mal an", 90, 100)), 110));

    [Fact]
    public void ShortGapsAreNotPauses() =>
        Assert.Empty(Pauses.Find(Fixtures.TranscriptOf(Fixtures.TurnWithWords("eins zwei drei vier", 0, 5),
            Fixtures.TurnWithWords("fünf sechs sieben acht", 50, 55)), 60));

    [Fact]
    public void Thresholds()
    {
        // inner ≥ 60 s marked, ≥ 180 s suggests a split; trailing ≥ 60 s; leading only ≥ 180 s.
        var t = Fixtures.TranscriptOf(Fixtures.TurnWithWords("eins zwei drei vier", 180, 185),
            Fixtures.TurnWithWords("fünf sechs sieben acht", 245, 250),
            Fixtures.TurnWithWords("neun zehn elf zwölf", 429.9, 435));
        var p = Pauses.Find(t, 495);
        Assert.Equal(new[] { PauseKind.Leading, PauseKind.Inner, PauseKind.Inner, PauseKind.Trailing }, p.Select(x => x.Kind));
        Assert.False(p[1].SuggestsSplit);          // 60 s
        Assert.False(p[2].SuggestsSplit);          // 179.9 s
        Assert.Equal(60, p[3].Duration);
        // No speech at all: one leading pause only from 180 s on.
        Assert.Empty(Pauses.Find(Fixtures.TranscriptOf(), 179));
        Assert.Single(Pauses.Find(Fixtures.TranscriptOf(), 180));
        // Turns without words count words from the text.
        Assert.True(Pauses.IsMinor(new Turn { Text = "eins zwei drei" }));
        Assert.False(Pauses.IsMinor(new Turn { Text = "eins zwei drei vier" }));
    }

    [Fact]
    public void LengthTextAndSuggestion()
    {
        Assert.Equal("119 s", Pauses.LengthText(119.4));
        Assert.Equal("2 min", Pauses.LengthText(120));
        Assert.Equal("3 min", Pauses.LengthText(150));
        var best = Pauses.LongestSplitSuggestion(new[] { new Pause(0, 200, PauseKind.Inner), new Pause(300, 700, PauseKind.Inner), new Pause(800, 2000, PauseKind.Trailing) });
        Assert.Equal(300, best!.Value.Start);
    }
}

public class SliceTests
{
    [Fact]
    public void SliceKeepsWordsByMidpointAndShifts()
    {
        var t = Fixtures.TranscriptOf(Fixtures.TurnWithWords("a b c d", 8, 12), Fixtures.TurnWithWords("e f", 20, 22, "s2"));
        t.Speakers = new List<Speaker> { new() { Id = "s1", Label = "A" }, new() { Id = "s2", Label = "B" } };
        var s = SessionEdit.Slice(t, 10, 15);
        Assert.Single(s.Turns);
        Assert.Equal("c d", s.Turns[0].Text);
        Assert.Equal(0, s.Turns[0].Start);
        Assert.Equal(2, s.Turns[0].End);
        Assert.Equal(new[] { "s1" }, s.Speakers.Select(x => x.Id));
        // Turns without word timings are kept when their middle lies inside.
        var t2 = Fixtures.TranscriptOf(new Turn { Speaker = "s1", Start = 9, End = 12, Text = "x" }, new Turn { Speaker = "s1", Start = 4, End = 11, Text = "y" });
        var s2 = SessionEdit.Slice(t2, 10, 20);
        Assert.Equal(new[] { "x" }, s2.Turns.Select(x => x.Text));
        Assert.Equal(0, s2.Turns[0].Start);
        Assert.Equal(2, s2.Turns[0].End);
    }

    [Fact]
    public void AssistLinesAndSecondPart()
    {
        var record = new JsonObject
        {
            ["context"] = new JsonObject { ["clientTitle"] = "MUSTER SOLAR" },
            ["state"] = new JsonObject(),
            ["lines"] = new JsonArray(
                new JsonObject { ["start"] = 10.0, ["speaker"] = "A", ["text"] = "eins" },
                new JsonObject { ["start"] = 70.0, ["speaker"] = "B", ["text"] = "zwei" }),
            ["recapRecordID"] = "rec1", ["recapUploadedAt"] = "2026-11-10T08:00:00Z", ["recapDraft"] = new JsonArray(),
            ["createdContactIDs"] = new JsonArray("c1"), ["speakersCarried"] = true,
        };
        var r2 = SessionEdit.SecondPart(record, 60, double.PositiveInfinity);
        Assert.Equal(new[] { "context", "createdContactIDs", "lines", "speakersCarried", "state" }, r2.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        var lines = r2["lines"]!.AsArray();
        Assert.Single(lines);
        Assert.Equal(10, lines[0]!["start"]!.GetValue<double>());
        Assert.Empty(r2["createdContactIDs"]!.AsArray());
    }
}

[CollectionDefinition("StorageAppData", DisableParallelization = true)]
public class StorageAppDataCollection { }

/// Tests that touch the app data folder: each points AppPaths at its own temporary folder.
[Collection("StorageAppData")]
public sealed class SessionFileTests : IDisposable
{
    readonly string _home = Path.Combine(Path.GetTempPath(), "ma-storage-" + Guid.NewGuid().ToString("N"));

    public SessionFileTests() => AppPaths.OverrideRoot(_home);

    public void Dispose()
    {
        AppPaths.OverrideRoot(null);
        try { Directory.Delete(_home, true); } catch { }
    }

    [Fact]
    public void SessionFoldersAreUniqueWithinOneSecond()
    {
        var baseName = SessionStore.FolderName(DateTime.UnixEpoch.AddSeconds(2_000_000));
        var a = SessionStore.CreateSessionFolder(baseName);
        var b = SessionStore.CreateSessionFolder(baseName);
        var c = SessionStore.CreateSessionFolder(baseName);
        Assert.Equal(baseName, a);
        Assert.Equal(baseName + "_2", b);
        Assert.Equal(baseName + "_3", c);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$", baseName);
    }

    [Fact]
    public void NoModelsFolderIsCreated()
    {
        AppPaths.Ensure();
        SessionStore.CreateSessionFolder("x");
        Assert.False(Directory.Exists(Path.Combine(AppPaths.Root, "Models")));
    }

    [Fact]
    public void WavTracksAreReadAndCafWins()
    {
        var id = SessionStore.CreateSessionFolder("wav-test");
        Fixtures.WriteWav(Path.Combine(AppPaths.Session(id), "mic.wav"), 3 * 8000);
        Assert.EndsWith("mic.wav", AppPaths.TrackFile(id, Track.Mic));
        Assert.True(Math.Abs(SessionStore.AudioDuration(id, new[] { Track.Mic }) - 3) < 0.01);
        Fixtures.WriteCafInt16(Path.Combine(AppPaths.Session(id), "mic.caf"), 2 * 8000);
        Assert.EndsWith("mic.caf", AppPaths.TrackFile(id, Track.Mic));
        Assert.True(Math.Abs(SessionStore.AudioDuration(id, new[] { Track.Mic }) - 2) < 0.01);
    }

    [Fact]
    public void InterruptedRecordingGetsItsLength()
    {
        var id = SessionStore.CreateSessionFolder("crash-test");
        Fixtures.WriteWav(AppPaths.NewTrack(id, Track.Mic), 4 * 8000);
        Fixtures.WriteWav(AppPaths.NewTrack(id, Track.System), 5 * 8000);
        var info = new SessionInfo { Id = id, Title = "Crash", StartedAt = DateTime.UtcNow, Mode = MeetingMode.Call, Tracks = { Track.Mic, Track.System }, Status = SessionStatus.Recording };
        var r = SessionStore.RecoverInterrupted(info);
        Assert.Equal(SessionStatus.Recorded, r.Status);
        Assert.True(Math.Abs(r.DurationSec - 5) < 0.01);
    }

    [Fact]
    public void UnfinishedWavHeaderIsReadToTheEnd()
    {
        // A crash before the header was patched: data size 0, real samples behind it.
        var id = SessionStore.CreateSessionFolder("crash-wav");
        var path = AppPaths.NewTrack(id, Track.Mic);
        Fixtures.WriteWav(path, 16_000, 16_000);
        using (var fs = new FileStream(path, FileMode.Open)) { fs.Position = 40; fs.Write(new byte[4]); }
        Assert.True(Math.Abs(AudioContainer.Probe(path).Seconds - 1) < 0.001);
        // Unreadable header: estimated from the size (16 kHz 16-bit after 4096 bytes).
        var junk = Path.Combine(AppPaths.Session(id), "system.wav");
        File.WriteAllBytes(junk, new byte[4096 + 32_000]);
        Assert.Equal(1, AudioContainer.DurationOrEstimate(junk), 3);
    }

    [Fact]
    public void ListSortsNewestFirstAndSkipsBrokenFolders()
    {
        foreach (var (id, h) in new[] { ("a", 1), ("b", 3), ("c", 2) })
        {
            SessionStore.CreateSessionFolder(id);
            SessionStore.Save(new SessionInfo { Id = id, Title = id, StartedAt = new DateTime(2026, 1, 1, h, 0, 0, DateTimeKind.Utc), Tracks = { Track.Mic } });
        }
        Directory.CreateDirectory(AppPaths.Session("broken"));
        File.WriteAllText(AppPaths.Info("broken"), "{ nope");
        Directory.CreateDirectory(AppPaths.Session("empty"));
        Assert.Equal(new[] { "b", "c", "a" }, SessionStore.List().Select(s => s.Id));
        SessionStore.Rename("a", "Neu");
        Assert.Equal("Neu", SessionStore.Load("a").Title);
        SessionStore.Delete("a");
        Assert.False(Directory.Exists(AppPaths.Session("a")));
    }

    static void MakeSession(string id, double seconds, SessionStatus status = SessionStatus.Done, bool caf = false, bool system = false)
    {
        Directory.CreateDirectory(AppPaths.Session(id));
        Func<int, short> ramp = i => (short)(i % 30_000);
        if (caf) Fixtures.WriteCafInt16(Path.Combine(AppPaths.Session(id), "mic.caf"), (int)(seconds * 8000), 8000, ramp);
        else Fixtures.WriteWav(AppPaths.NewTrack(id, Track.Mic), (int)(seconds * 8000), 8000, ramp);
        if (system) Fixtures.WriteWav(AppPaths.NewTrack(id, Track.System), (int)(seconds * 8000), 8000, ramp);
        var tracks = system ? new List<Track> { Track.Mic, Track.System } : new List<Track> { Track.Mic };
        SessionStore.Save(new SessionInfo { Id = id, Title = "Two", StartedAt = DateTime.UnixEpoch.AddSeconds(1_000_000), DurationSec = seconds, Mode = MeetingMode.InPerson, Tracks = tracks, Status = status });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitsAudioAndTranscriptAndRemovesThePause(bool caf)
    {
        var id = "2026-01-01_10-00-00";
        MakeSession(id, 400, caf: caf);
        var start = DateTime.UnixEpoch.AddSeconds(1_000_000);
        var tr = new Transcript
        {
            Turns =
            {
                new Turn { Speaker = "s1", Start = 0, End = 100, Text = "erstes meeting endet hier jetzt", Track = Track.Mic },
                new Turn { Speaker = "s1", Start = 350, End = 390, Text = "zweites meeting fängt jetzt an", Track = Track.Mic },
            },
            Speakers = { new Speaker { Id = "s1", Label = "A" } }, Engine = "t", CreatedAt = start,
        };
        SessionStore.SaveTranscript(tr, id);

        var pause = Pauses.Find(tr, 400).First(p => p.Kind == PauseKind.Inner);
        var second = SessionEdit.Split(id, pause, removePause: true);

        var first = SessionStore.Load(id);
        Assert.True(Math.Abs(first.DurationSec - 102) < 0.01);
        Assert.True(Math.Abs(second.DurationSec - 52) < 0.01);
        Assert.Equal(start.AddSeconds(348), second.StartedAt);
        Assert.Equal("Two (Part 2)", second.Title);
        var micFirst = AppPaths.TrackFile(id, Track.Mic);
        Assert.EndsWith(caf ? ".caf" : ".wav", micFirst);                        // trim keeps the container
        Assert.Equal(102 * 8000, AudioContainer.Probe(micFirst).Frames);
        var micSecond = AppPaths.TrackFile(second.Id, Track.Mic);
        Assert.EndsWith(".wav", micSecond);                                        // new tracks are WAV
        Assert.Equal(52 * 8000, AudioContainer.Probe(micSecond).Frames);
        Assert.Equal(8000, AudioContainer.Probe(micSecond).SampleRate);
        // Sample-exact: part 2 starts with source frame 348 × 8000.
        var samples = AudioFile.Read(micSecond);
        Assert.Equal((348 * 8000) % 30_000 / 32768.0, samples[0], 3);
        Assert.Equal(new[] { "erstes meeting endet hier jetzt" }, SessionStore.LoadTranscript(id)!.Turns.Select(t => t.Text));
        var t2 = SessionStore.LoadTranscript(second.Id)!;
        Assert.Equal(new[] { "zweites meeting fängt jetzt an" }, t2.Turns.Select(t => t.Text));
        Assert.True(Math.Abs(t2.Turns[0].Start - 2) < 0.01);
        Assert.False(File.Exists(Path.Combine(AppPaths.Session(id), ".trim-mic" + (caf ? ".caf" : ".wav"))));
    }

    [Fact]
    public void SplitGivesPartTwoItsOwnAssistRecord()
    {
        var id = SessionStore.CreateSessionFolder("assist-split");
        MakeSession(id, 100, SessionStatus.Recorded);
        var record = new JsonObject
        {
            ["context"] = new JsonObject { ["clientTitle"] = "MUSTER SOLAR", ["myName"] = "Max Beispiel" },
            ["state"] = new JsonObject(),
            ["lines"] = new JsonArray(new JsonObject { ["start"] = 10, ["speaker"] = "A", ["text"] = "eins" },
                                      new JsonObject { ["start"] = 70, ["speaker"] = "B", ["text"] = "zwei" }),
            ["recapRecordID"] = "rec1", ["recapUploadedAt"] = "2026-11-10T08:00:00Z", ["recapDraft"] = new JsonArray(),
            ["createdContactIDs"] = new JsonArray(),
        };
        JsonIO.WriteText(AppPaths.AssistFile(id), JsonIO.Format(record));

        var second = SessionEdit.Split(id, 60);
        var first = JsonNode.Parse(File.ReadAllText(AppPaths.AssistFile(id)))!;
        Assert.Equal(new[] { "eins" }, first["lines"]!.AsArray().Select(l => l!["text"]!.GetValue<string>()));
        Assert.Equal("rec1", first["recapRecordID"]!.GetValue<string>());
        var r2 = JsonNode.Parse(File.ReadAllText(AppPaths.AssistFile(second.Id)))!;
        Assert.Equal(new[] { "zwei" }, r2["lines"]!.AsArray().Select(l => l!["text"]!.GetValue<string>()));
        Assert.Equal(10, r2["lines"]![0]!["start"]!.GetValue<double>(), 3);
        Assert.Equal("MUSTER SOLAR", r2["context"]!["clientTitle"]!.GetValue<string>());
        Assert.Null(r2["recapRecordID"]);
        Assert.Null(r2["recapUploadedAt"]);
        Assert.Null(r2["recapDraft"]);
        Assert.Equal(SessionStatus.Recorded, second.Status);
    }

    [Fact]
    public void TrimRules()
    {
        MakeSession("t", 10, system: true);
        SessionStore.SaveTranscript(Fixtures.TranscriptOf(Fixtures.TurnWithWords("eins zwei drei vier", 2, 4)), "t");
        // < 1 s left → refused; busy sessions refused.
        Assert.Equal("The chosen part is empty.", Assert.Throws<SessionEditException>(() => SessionEdit.Trim("t", 5, 5.9)).Message);
        var info = SessionEdit.Trim("t", 1, 50);       // end clamped to the duration
        Assert.Equal(9, info.DurationSec, 3);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1_000_001), info.StartedAt);
        Assert.Equal(9 * 8000, AudioContainer.Probe(AppPaths.TrackFile("t", Track.System)).Frames);
        Assert.Equal(1, SessionStore.LoadTranscript("t")!.Turns[0].Start, 3);
        SessionStore.SetStatus("t", SessionStatus.Queued);
        Assert.Equal("The recording is being recorded or transcribed right now.",
            Assert.Throws<SessionEditException>(() => SessionEdit.Trim("t", 0, 5)).Message);
        Assert.Throws<SessionEditException>(() => SessionEdit.Split("t", 4));
        SessionStore.SetStatus("t", SessionStatus.Done);
        Assert.Throws<SessionEditException>(() => SessionEdit.Split("t", 0.9));
        Assert.Throws<SessionEditException>(() => SessionEdit.Split("t", 8.1));
    }

    [Fact]
    public void TrimKeepsLateAssistLinesWhenTheEndStays()
    {
        MakeSession("a", 100);
        var record = new JsonObject
        {
            ["lines"] = new JsonArray(new JsonObject { ["start"] = 5, ["speaker"] = "A", ["text"] = "früh" },
                                      new JsonObject { ["start"] = 50, ["speaker"] = "A", ["text"] = "mitte" },
                                      new JsonObject { ["start"] = 100.5, ["speaker"] = "A", ["text"] = "spät" }),
        };
        JsonIO.WriteText(AppPaths.AssistFile("a"), JsonIO.Format(record));
        SessionEdit.Trim("a", 10, 100);
        var lines = JsonNode.Parse(File.ReadAllText(AppPaths.AssistFile("a")))!["lines"]!.AsArray();
        Assert.Equal(new[] { "mitte", "spät" }, lines.Select(l => l!["text"]!.GetValue<string>()));
        Assert.Equal(40, lines[0]!["start"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void SplitIdsComeFromPartTwoStartWithSuffix()
    {
        MakeSession("orig", 20);
        var expected = SessionStore.FolderName(DateTime.UnixEpoch.AddSeconds(1_000_010));
        Directory.CreateDirectory(AppPaths.Session(expected));       // taken
        var second = SessionEdit.Split("orig", 10);
        Assert.Equal(expected + "_2", second.Id);
    }

    [Fact]
    public void ImportCreatesASession()
    {
        var dir = Path.Combine(_home, "in");
        Directory.CreateDirectory(dir);
        Fixtures.WriteWav(Path.Combine(dir, "Weekly Muster.wav"), 8000 * 3, 8000);
        Fixtures.WriteWav(Path.Combine(dir, "call.wav"), 8000 * 4, 8000);
        var one = SessionImport.ImportFiles(Path.Combine(dir, "Weekly Muster.wav"), null, MeetingMode.Call);
        Assert.StartsWith("import_", one.Id);
        Assert.Equal("Weekly Muster", one.Title);
        Assert.Equal(MeetingMode.InPerson, one.Mode);                // no system file → in person
        Assert.Equal(SessionStatus.Recorded, one.Status);
        Assert.Equal(3, one.DurationSec, 2);
        Assert.Equal(16_000, AudioContainer.Probe(AppPaths.NewTrack(one.Id, Track.Mic)).SampleRate);
        var two = SessionImport.ImportFiles(Path.Combine(dir, "Weekly Muster.wav"), Path.Combine(dir, "call.wav"), SessionImport.ModeFor(2));
        Assert.Equal(MeetingMode.Call, two.Mode);
        Assert.Equal(new[] { Track.Mic, Track.System }, two.Tracks);
        Assert.Equal(4, two.DurationSec, 2);                          // the longer track
        Assert.NotEqual(one.Id, two.Id);
    }

    [Fact]
    public void LaunchRecovery()
    {
        MakeSession("rec", 0, SessionStatus.Recording);
        Fixtures.WriteWav(AppPaths.NewTrack("rec", Track.Mic), 8000 * 7, 8000);
        MakeSession("q1", 5, SessionStatus.Transcribing);
        MakeSession("done", 5, SessionStatus.Done);
        var queued = new List<string>();
        Assert.Equal(new[] { "q1" }, MeetingAssistant.Core.Storage.LaunchRecovery.Run(queued.Add));
        Assert.Equal(new[] { "q1" }, queued);
        Assert.Equal(SessionStatus.Recorded, SessionStore.Load("rec").Status);
        Assert.Equal(7, SessionStore.Load("rec").DurationSec, 2);
        Assert.Equal(SessionStatus.Done, SessionStore.Load("done").Status);
    }

    [Fact]
    public void PreferencesDefaultsAndExplicitNullShortcut()
    {
        var store = new SettingsStore(Path.Combine(_home, "preferences.json"));
        var p = Preferences.Load(store);
        Assert.Equal("parakeet-ultra", p.Engine);
        Assert.Equal("auto", p.Language);
        Assert.Equal("Me", p.MyName);
        Assert.Equal(MeetingMode.Call, p.DefaultMode);
        Assert.True(p.LiveTranscription && p.AutoTranscribe && p.DictationEnabled && p.ScribePlusContext && p.ScribePlusWarm && p.FitToCursor && p.MuteWhileDictating);
        Assert.Equal("sonnet", p.ScribePlusModel);
        Assert.Equal(HotKeySetting.DefaultPlain, p.DictationHotKey);
        Assert.Equal(new[] { HotKeySetting.VkRControl, HotKeySetting.VkRShift }, p.ScribePlusHotKey!.HeldKeys);

        // Missing keys fall back to defaults; an explicit null shortcut stays "not set".
        store.Set(SettingsStore.PreferencesKey, JsonNode.Parse("{\"myName\":\"Erika Musterfrau\",\"dictationHotKey\":null,\"engine\":\"bogus\",\"defaultMode\":\"hybrid\"}"));
        p = Preferences.Load(store);
        Assert.Equal("Erika Musterfrau", p.MyName);
        Assert.Null(p.DictationHotKey);
        Assert.NotNull(p.ScribePlusHotKey);
        Assert.Equal("parakeet-ultra", p.Engine);
        Assert.Equal(MeetingMode.Hybrid, p.DefaultMode);

        p.InputDeviceUID = "{0.0.1.00000000}.{abc}";
        p.Save(store);
        var reread = Preferences.Load(new SettingsStore(store.FilePath));
        Assert.Null(reread.DictationHotKey);
        Assert.Equal("{0.0.1.00000000}.{abc}", reread.InputDeviceUID);
        var json = store.Get(SettingsStore.PreferencesKey)!.AsObject();
        Assert.True(json.ContainsKey("dictationHotKey"));              // written as null
        Assert.Null(json["dictationHotKey"]);
        Assert.Equal(16, json.Count);
        Assert.True(reread.HideFromScreenSharing);                     // on by default
        Assert.False(Preferences.FromJson(JsonNode.Parse("""{"hideFromScreenSharing":false}""")).HideFromScreenSharing);

        store.DismissSplitHint("s1");
        store.DismissSplitHint("s2");
        store.DismissSplitHint("s1");
        Assert.Equal("s1,s2", store.GetString(SettingsStore.DismissedSplitHintsKey));
        Assert.Equal(" ", new Preferences { MyName = " " }.MyName);
        Assert.Equal("Me", new Preferences { MyName = " " }.EffectiveName);
    }

    [Fact]
    public void DictationCafIsReadableAndMacLaidOut()
    {
        var path = Path.Combine(_home, "d.caf");
        Directory.CreateDirectory(_home);
        var samples = Enumerable.Range(0, 1000).Select(i => (float)Math.Sin(i * 0.1) * 0.5f).ToArray();
        AudioContainer.WriteCafFloat32(path, samples);
        var info = AudioContainer.Probe(path);
        Assert.Equal(4096, info.DataOffset);
        Assert.True(info.IsFloat);
        Assert.Equal(1000, info.Frames);
        var back = AudioFile.Read(path);
        Assert.Equal(samples, back);
    }
}

public class MacJsonFormatTests
{
    [Fact]
    public void FilesEscapeSlashesLikeTheMacPromptsDoNot()
    {
        // The Mac's file encoder writes "/" as "\/" (no .withoutEscapingSlashes); compact prompt JSON does not.
        var info = new SessionInfo { Id = "x", Title = "Win / Pain", StartedAt = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc), Tracks = { Track.Mic } };
        Assert.Contains("\"title\" : \"Win \\/ Pain\"", JsonIO.Serialize(info));
        Assert.Equal("Win / Pain", JsonIO.Deserialize<SessionInfo>(JsonIO.Serialize(info)).Title);
        Assert.Equal("{\"a\":\"x/y\"}", JsonIO.Compact(new JsonObject { ["a"] = "x/y" }));
    }
}
