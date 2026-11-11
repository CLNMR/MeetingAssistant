using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;
using Xunit;

namespace MeetingAssistant.Core.Tests.Pipeline;

public class LiveSlotTests
{
    static float[] Frames(int frames, params (int From, int To, int Slot, float Weight)[] fills)
    {
        var p = new float[frames * LiveSlots.SlotCount];
        foreach (var (from, to, slot, w) in fills) for (int f = from; f < to; f++) p[f * LiveSlots.SlotCount + slot] = w;
        return p;
    }

    [Fact]
    public void DominantSlotOverTheWidenedWord()
    {
        var p = Frames(200, (0, 100, 2, 1), (100, 200, 5, 1));
        Assert.Equal(2, LiveSlots.SlotFor(new Word("a", 0.2, 0.5), p, 200));
        Assert.Equal(5, LiveSlots.SlotFor(new Word("b", 1.2, 1.5), p, 200));
    }

    [Fact]
    public void NullWhileTheDiarizerLags()
    {
        var p = Frames(100, (0, 100, 1, 1));
        // word ends at 0.9 s → frame 90 + 5 = 95 ≤ 100: known; 0.96 s → 101 > 100: lagging
        Assert.Equal(1, LiveSlots.SlotFor(new Word("a", 0.8, 0.9), p, 100));
        Assert.Null(LiveSlots.SlotFor(new Word("b", 0.9, 0.96), p, 100));
    }

    [Fact]
    public void WeakSlotsGiveNull()
    {
        // sum must exceed 0.1 × frame count: 30 frames (word 0.5–0.7 widened) × 0.1 = 3
        var weak = Frames(200, (40, 80, 3, 0.1f));
        Assert.Null(LiveSlots.SlotFor(new Word("a", 0.5, 0.7), weak, 200));
        var enough = Frames(200, (40, 80, 3, 0.11f));
        Assert.Equal(3, LiveSlots.SlotFor(new Word("a", 0.5, 0.7), enough, 200));
    }

    [Fact]
    public void MeSlotLocksAtTwentyFiveWordsAndTwiceTheRunnerUp()
    {
        var me = new MeSlotTracker();
        Assert.Equal("me", me.MicSpeaker(4));                  // unknown me slot: every mic word is me
        Assert.Null(me.MicSpeaker(null));
        me.Update(Enumerable.Repeat<int?>(1, 24).Concat(Enumerable.Repeat<int?>(2, 5)));
        Assert.Equal(1, me.MeSlot);
        Assert.False(me.Locked);                              // 24 < 25
        Assert.Equal("mic-2", me.MicSpeaker(2));
        me.Update(Enumerable.Repeat<int?>(1, 25).Concat(Enumerable.Repeat<int?>(2, 13)));
        Assert.False(me.Locked);                              // 25 < 2 × 13
        me.Update(Enumerable.Repeat<int?>(2, 30).Concat(Enumerable.Repeat<int?>(1, 25)));
        Assert.Equal(2, me.MeSlot);                           // may still move
        me.Update(Enumerable.Repeat<int?>(1, 26).Concat(Enumerable.Repeat<int?>(2, 13)).Append(null));
        Assert.True(me.Locked);
        me.Update(Enumerable.Repeat<int?>(2, 100));
        Assert.Equal(1, me.MeSlot);                           // locked: never changes again
    }

    [Fact]
    public void LinesSplitAtSpeakerChangesAndInheritUnknown()
    {
        var u = new LiveUtterance("mic-0", Track.Mic, new[]
        {
            new Word("Hallo", 0, 0.3), new Word("zusammen", 0.4, 0.8), new Word("ja", 1.0, 1.2), new Word("genau", 1.3, 1.6),
        }, true);
        var speakers = new Dictionary<string, string?> { ["Hallo"] = "me", ["zusammen"] = null, ["ja"] = "mic-1", ["genau"] = null };
        var lines = LiveLineBuilder.Build(new[] { u }, (w, _) => speakers[w.W]);
        Assert.Equal(new[] { "mic-0.0", "mic-0.1" }, lines.Select(l => l.Id));
        Assert.Equal(new[] { "Hallo zusammen", "ja genau" }, lines.Select(l => l.Text));
        Assert.Equal(new[] { "me", "mic-1" }, lines.Select(l => l.Speaker));
        Assert.Equal(0.8, lines[0].End);
    }

    [Fact]
    public void FirstUnknownWordStaysUnknownAndLinesSortByStart()
    {
        var a = new LiveUtterance("system-1", Track.System, new[] { new Word("später", 5, 5.5) }, false);
        var b = new LiveUtterance("system-0", Track.System, new[] { new Word("vorher", 1, 1.5) }, true);
        var lines = LiveLineBuilder.Build(new[] { a, b }, (_, _) => null);
        Assert.Equal(new[] { "vorher", "später" }, lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Null(l.Speaker));
        Assert.False(lines[1].IsFinal);
    }

    [Fact]
    public void EchoWordsAreHiddenOnTheMicOnly()
    {
        var sys = new LiveUtterance("system-0", Track.System, new[] { new Word("Angebot", 2, 2.5), new Word("morgen", 2.6, 3) }, true);
        var mic = new LiveUtterance("mic-0", Track.Mic, new[] { new Word("Angebot", 2.4, 2.9), new Word("morgen", 3.0, 3.3), new Word("Danke", 9, 9.4) }, true);
        var echo = LiveLineBuilder.EchoWords(new[] { sys, mic });
        Assert.Equal(2, echo.Count);
        var lines = LiveLineBuilder.Build(new[] { sys, mic }, (_, t) => t == Track.Mic ? "me" : "system-0", echo);
        Assert.Equal(new[] { "Angebot morgen", "Danke" }, lines.Select(l => l.Text));
    }
}

public class AssemblyTests
{
    static Word Wd(string s, double a) => new(s, a, a + 0.3);

    static TrackAnalysis Mic(params (string Speaker, double Start)[] words) => new()
    {
        Track = Track.Mic,
        Words = words.Select(w => Wd("wort", w.Start)).ToList(),
        Segments = words.GroupBy(w => w.Speaker).Select(g => new SpeakerSegment(g.Key, new TimeRange(g.Min(x => x.Start), g.Max(x => x.Start) + 0.3))).ToList(),
    };

    [Fact]
    public void CallModeMeIsTheMicSpeakerWithMostWords()
    {
        var mic = Mic(("mic-0", 0), ("mic-1", 5), ("mic-1", 5.4), ("mic-1", 5.8));
        var t = TranscriptAssembly.Build(new[] { mic }, MeetingMode.Call, null, new PipelineSettings { MyName = "Max Beispiel" }, null, new SpeakerLibrary(), null);
        Assert.Equal(new[] { "Speaker 1", "Max Beispiel" }, t.Speakers.Select(s => s.Label));
        Assert.True(t.Speakers.Single(s => s.Id == "mic-1").IsMe);
        // In person: no voiceprint → nobody is me
        var room = TranscriptAssembly.Build(new[] { mic }, MeetingMode.InPerson, null, new PipelineSettings(), null, new SpeakerLibrary(), null);
        Assert.DoesNotContain(room.Speakers, s => s.IsMe);
    }

    [Fact]
    public void VoiceprintPicksMeAmongMicClustersOnly()
    {
        var mic = Mic(("mic-0", 0), ("mic-1", 5));
        mic.Centroids["mic-0"] = new float[] { 1, 0, 0 };
        mic.Centroids["mic-1"] = new float[] { 0, 1, 0 };
        var vp = new Voiceprint { Name = "Max Beispiel", Embedding = new float[] { 0.1f, 1, 0 } };
        var t = TranscriptAssembly.Build(new[] { mic }, MeetingMode.InPerson, null, new PipelineSettings { MyName = "" }, vp, new SpeakerLibrary(), null);
        var me = t.Speakers.Single(s => s.IsMe);
        Assert.Equal("mic-1", me.Id);
        Assert.Equal("Me", me.Label);   // empty name → "Me"
    }

    [Fact]
    public void LibraryNamesAreUsedOnceAndUnnamedAreCountedSeparately()
    {
        var mic = Mic(("mic-0", 0), ("mic-1", 5), ("mic-2", 10));
        mic.Centroids["mic-0"] = new float[] { 1, 0, 0 };
        mic.Centroids["mic-1"] = new float[] { 0, 0, 1 };
        mic.Centroids["mic-2"] = new float[] { 0, 1, 0 };
        var lib = new SpeakerLibrary();
        lib.Entries.Add(new SpeakerLibraryEntry { Name = "Erika Musterfrau", Embedding = new float[] { 0, 0, 1 }, Count = 1 });
        var t = TranscriptAssembly.Build(new[] { mic }, MeetingMode.InPerson, null, new PipelineSettings(), null, lib, null);
        Assert.Equal(new[] { "Speaker 1", "Erika Musterfrau", "Speaker 2" }, t.Speakers.Select(s => s.Label));
    }

    [Fact]
    public void LibraryNeedsFiftyFivePercent()
    {
        var mic = Mic(("mic-0", 0));
        mic.Centroids["mic-0"] = new float[] { 1, 0 };
        var lib = new SpeakerLibrary();
        lib.Entries.Add(new SpeakerLibraryEntry { Name = "Lena Beispiel", Embedding = new[] { 0.54f, 0.8417f }, Count = 1 });   // cosine 0.54
        Assert.Equal("Speaker 1", TranscriptAssembly.Build(new[] { mic }, MeetingMode.InPerson, null, new PipelineSettings(), null, lib, null).Speakers[0].Label);
        lib.Entries[0].Embedding = new[] { 0.56f, 0.8285f };
        Assert.Equal("Lena Beispiel", TranscriptAssembly.Build(new[] { mic }, MeetingMode.InPerson, null, new PipelineSettings(), null, lib, null).Speakers[0].Label);
    }

    [Fact]
    public void HybridIgnoresTheParticipantsTarget()
    {
        var mic = Mic(("mic-0", 0), ("mic-1", 5));
        mic.Centroids["mic-0"] = new float[] { 1, 0 };
        mic.Centroids["mic-1"] = new float[] { 0.6f, 0.8f };
        mic.SpeechSeconds["mic-0"] = 10; mic.SpeechSeconds["mic-1"] = 5;
        Assert.Single(TranscriptAssembly.Build(new[] { mic }, MeetingMode.InPerson, 1, new PipelineSettings(), null, new SpeakerLibrary(), null).Speakers);
        Assert.Equal(2, TranscriptAssembly.Build(new[] { mic }, MeetingMode.Hybrid, 1, new PipelineSettings(), null, new SpeakerLibrary(), null).Speakers.Count);
    }

    [Fact]
    public void EchoIsRemovedInCallModeButNotInPerson()
    {
        var sys = new TrackAnalysis { Track = Track.System, Words = new[] { new Word("Angebot", 1, 1.4), new Word("morgen", 1.5, 1.9) } };
        var mic = new TrackAnalysis { Track = Track.Mic, Words = new[] { new Word("Angebot", 1.2, 1.6), new Word("morgen", 1.7, 2.1) } };
        var call = TranscriptAssembly.Build(new[] { mic, sys }, MeetingMode.Call, null, new PipelineSettings(), null, new SpeakerLibrary(), null);
        Assert.Equal(new[] { "system-0" }, call.Speakers.Select(s => s.Id));
        var room = TranscriptAssembly.Build(new[] { mic, sys }, MeetingMode.InPerson, null, new PipelineSettings(), null, new SpeakerLibrary(), null);
        Assert.Equal(2, room.Speakers.Count);
    }

    [Fact]
    public void CleanupDropsCreditsAppliesVocabularyAndRenumbers()
    {
        var mic = new TrackAnalysis { Track = Track.Mic, Words = new[] { new Word("Untertitel", 0, 0.5), new Word("ZDF", 0.6, 1), new Word("beispilop", 5, 5.5) } };
        var cleanup = new TranscriptTextCleanup(text => text == "Untertitel ZDF",
            words => words.Select(w => w.W == "beispilop" ? w with { W = "Beispilop" } : w).ToList());
        var t = TranscriptAssembly.Build(new[] { mic }, MeetingMode.InPerson, null, new PipelineSettings(), null, new SpeakerLibrary(), cleanup);
        Assert.Single(t.Turns);
        Assert.Equal("Beispilop", t.Turns[0].Text);
        Assert.Equal(0, t.Turns[0].Id);
    }
}

public class StoreAndLevelTests
{
    [Fact]
    public void EnrollmentWindowsAreSixSecondsWhileThreeRemain()
    {
        // a window is used while more than 3 s remain
        Assert.Equal(new[] { (0, 96_000), (96_000, 144_001) }, VoiceprintStore.EnrollmentWindows(144_001));
        Assert.Equal(new[] { (0, 96_000) }, VoiceprintStore.EnrollmentWindows(144_000));
        Assert.Equal(new[] { (0, 48_001) }, VoiceprintStore.EnrollmentWindows(48_001));
        Assert.Empty(VoiceprintStore.EnrollmentWindows(48_000));
        Assert.Equal(15, VoiceprintStore.RecordingSeconds);
        Assert.Equal(8, VoiceprintStore.MinimumSpeechSeconds);
        Assert.Equal("Only 5 s of speech detected; at least 8 s are needed. Please record again and keep reading without long pauses.",
            VoiceprintStore.TooShortMessage(5.2));
    }

    [Fact]
    public void GainBoostsQuietAudioOnly()
    {
        Assert.Equal(1, AudioLevel.GainFor(0.2f, 0.5f));          // loud: never attenuate
        Assert.Equal(10, AudioLevel.GainFor(0.01f, 0), 3);         // 0.1 / 0.01
        Assert.Equal(20, AudioLevel.GainFor(0.001f, 0), 3);        // capped at 20×
        Assert.Equal(0.99f / 0.05f, AudioLevel.GainFor(0.001f, 0.05f), 3);   // peak limit
        Assert.Equal(1, AudioLevel.GainFor(0, 0));                 // silence
        var quiet = Enumerable.Range(0, 32_000).Select(i => (float)(0.01 * Math.Sin(i * 0.05))).ToArray();
        var g = AudioLevel.Gain(quiet);
        Assert.InRange(g, 7, 15);
    }
}
