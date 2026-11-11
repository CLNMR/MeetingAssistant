using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;
using Xunit;

namespace MeetingAssistant.Core.Tests.Pipeline;

// Ported from Tests/MeetingAssistantCoreTests/LogicTests.swift (Chunking, WordAlignment, Merger,
// SpeakerMath, SpeakerFix suites) plus tests for every numeric rule of spec 01-recording.

static class W
{
    public static Word Of(string s, double a, double b) => new(s, a, b);
}

public class ChunkingTests
{
    [Fact]
    public void JoinsCloseRegionsAndSplitsLongOnes()
    {
        var regions = new[] { new TimeRange(0, 2), new TimeRange(2.5, 4), new TimeRange(10, 40) };
        var c = Chunking.Chunks(regions, 14, 50, maxGap: 1.2, padding: 0);
        Assert.Equal(new TimeRange(0, 4), c[0]);
        Assert.All(c.Skip(1), x => Assert.True(x.Duration <= 14.0001));
        Assert.Equal(30, c.Skip(1).Sum(x => x.Duration), 3);
        Assert.Equal(3, c.Count - 1);   // ceil(30 / 14) equal parts
    }

    [Fact]
    public void PadsByDefaultAndClampsToTheAudio()
    {
        var c = Chunking.Chunks(new[] { new TimeRange(0.05, 1), new TimeRange(9.9, 10) }, 14, 10);
        Assert.Equal(2, c.Count);
        Assert.Equal(0, c[0].Start, 6);           // 0.05 − 0.15 clamped to 0
        Assert.Equal(1.15, c[0].End, 6);
        Assert.Equal(10, c[1].End, 6);            // 10 + 0.15 clamped to the duration
    }

    [Fact]
    public void IgnoresRegionsOfAtMostFiftyMilliseconds()
    {
        Assert.Empty(Chunking.Chunks(new[] { new TimeRange(1, 1.04) }, 14, 10, padding: 0));
        Assert.Single(Chunking.Chunks(new[] { new TimeRange(1, 1.06) }, 14, 10, padding: 0));
    }

    [Fact]
    public void JoinsOnlyWithinGapAndLength()
    {
        // gap below 1.2 s joins, 1.3 s does not
        Assert.Single(Chunking.Chunks(new[] { new TimeRange(0, 1), new TimeRange(2.19, 3) }, 14, 10, padding: 0));
        Assert.Equal(2, Chunking.Chunks(new[] { new TimeRange(0, 1), new TimeRange(2.3, 3) }, 14, 10, padding: 0).Count);
        // joining would exceed the max length
        Assert.Equal(2, Chunking.Chunks(new[] { new TimeRange(0, 10), new TimeRange(10.5, 15) }, 14, 20, padding: 0).Count);
    }

    [Fact]
    public void EngineChunkLengths()
    {
        Assert.Equal(14, AsrEngine.ParakeetUltra.MaxChunkSeconds());
        Assert.Equal(28, AsrEngine.Qwen3Large.MaxChunkSeconds());
        Assert.Equal("parakeet-ultra", AsrEngine.ParakeetUltra.Raw());
        Assert.Equal("qwen3-1.7b", AsrEngine.Qwen3Large.Raw());
        Assert.Equal(AsrEngine.ParakeetUltra, AsrEngineText.ParseEngine("something"));
        Assert.Null(AsrEngineText.ParseLanguage("french"));
        Assert.Equal("Automatic", TranscriptLanguage.Auto.Title());
    }
}

public class WordAlignmentTests
{
    [Fact]
    public void KeepsPunctuationAndUsesAlignerTimes()
    {
        var words = WordAlignment.Attach("Hallo, Welt! Wie geht's?",
            new[] { ("hallo", 0.1, 0.4), ("welt", 0.5, 0.8), ("wie", 1.0, 1.1), ("geht", 1.2, 1.3), ("s", 1.3, 1.4) }, 10, 12);
        Assert.Equal(new[] { "Hallo,", "Welt!", "Wie", "geht's?" }, words.Select(w => w.W));
        Assert.Equal(10.1, words[0].Start, 9);
        Assert.Equal(11.4, words[3].End, 9);
    }

    [Fact]
    public void InterpolatesUnmatchedWords()
    {
        var words = WordAlignment.Attach("eins zwei drei", new[] { ("eins", 0.0, 0.5), ("drei", 2.0, 2.5) }, 0, 3);
        Assert.Equal(3, words.Count);
        Assert.True(words[1].Start >= 0.5 && words[1].End <= 2);
    }

    [Fact]
    public void SpreadsEvenlyWithMinimumStep()
    {
        var w = WordAlignment.Spread("a b c d", 0, 2);
        Assert.Equal(new[] { 0, 0.5, 1, 1.5 }, w.Select(x => x.Start));
        var tiny = WordAlignment.Spread("a b", 5, 5);
        Assert.Equal(0.01, tiny[0].End - tiny[0].Start, 9);
    }

    [Fact]
    public void NormKeepsLettersAndDigitsLowercased() => Assert.Equal("größe3", WordAlignment.Norm("Größe-3!"));
}

public class MergerTests
{
    [Fact]
    public void AssignsByOverlapAndNearest()
    {
        var r = new TrackResult(Track.System, new[] { W.Of("a", 0, 0.5), W.Of("b", 1, 1.5), W.Of("c", 5.2, 5.5), W.Of("d", 20, 21) },
            new[] { new SpeakerSegment("s1", new(0, 2)), new SpeakerSegment("s2", new(5, 6)) }, null);
        Assert.Equal(new[] { "s1", "s1", "s2", "s2" }, Merger.AssignSpeakers(r).Select(x => x.Speaker));
    }

    [Fact]
    public void NearestSegmentOnlyWithinOneAndAHalfSeconds()
    {
        var segs = new[] { new SpeakerSegment("A", new(0, 1)), new SpeakerSegment("B", new(10, 11)) };
        // midpoint 2.45 is 1.45 s from A -> A; then 12.6 is 1.6 s from B -> previous word's speaker (A)
        var r = new TrackResult(Track.Mic, new[] { W.Of("x", 2.4, 2.5), W.Of("y", 12.55, 12.65) }, segs, null);
        Assert.Equal(new[] { "A", "A" }, Merger.AssignSpeakers(r).Select(x => x.Speaker));
        // the first word without any near segment takes the first segment's speaker
        var r2 = new TrackResult(Track.Mic, new[] { W.Of("z", 5, 5.1) }, segs, null);
        Assert.Equal("A", Merger.AssignSpeakers(r2)[0].Speaker);
    }

    [Fact]
    public void FixedSpeakerWhenNoSegments()
    {
        var r = new TrackResult(Track.Mic, new[] { W.Of("x", 0, 1) }, null, "me");
        Assert.Equal(new[] { "me" }, Merger.AssignSpeakers(r).Select(x => x.Speaker));
    }

    [Fact]
    public void SmoothsSingleWordFlips()
    {
        var r = new TrackResult(Track.Mic, new[] { W.Of("a", 0, 0.3), W.Of("b", 0.35, 0.6), W.Of("c", 0.65, 0.9) },
            new[] { new SpeakerSegment("A", new(0, 0.32)), new SpeakerSegment("B", new(0.33, 0.62)), new SpeakerSegment("A", new(0.63, 1)) }, null);
        Assert.Equal(new[] { "A", "A", "A" }, Merger.AssignSpeakers(r).Select(x => x.Speaker));
    }

    [Fact]
    public void SingletonSmoothingNeedsNeighboursWithinOneSecond()
    {
        var a = new List<(Word, string)> { (W.Of("a", 0, 0.3), "A"), (W.Of("b", 0.5, 0.8), "B"), (W.Of("c", 1.31, 1.5), "A") };
        Assert.Equal("B", Merger.SmoothSingletons(a)[1].Speaker);       // gap 1.01 s
        a[2] = (W.Of("c", 1.29, 1.5), "A");
        Assert.Equal("A", Merger.SmoothSingletons(a)[1].Speaker);       // gap 0.99 s
    }

    [Fact]
    public void RemovesEchoedRunsButKeepsOwnSpeech()
    {
        var system = new[] { W.Of("wir", 10, 10.2), W.Of("sind", 10.2, 10.4), W.Of("im", 10.4, 10.5), W.Of("plan", 10.5, 10.8) };
        var mic = new List<(Word, string)> { (W.Of("Wir", 10.1, 10.3), "me"), (W.Of("sind", 10.3, 10.5), "me"), (W.Of("im", 10.5, 10.6), "me"),
            (W.Of("Plan.", 10.6, 10.9), "me"), (W.Of("Danke", 15, 15.4), "me"), (W.Of("schön", 15.4, 15.8), "me") };
        Assert.Equal(new[] { "Danke", "schön" }, Merger.RemoveEcho(mic, system).Select(x => x.Word.W));
    }

    [Fact]
    public void EchoWindowIsTwoSecondsBeforeAndOneAfter()
    {
        var mic = new[] { W.Of("Angebot", 10, 10.5) };
        Assert.True(Merger.EchoMask(mic, new[] { W.Of("angebot", 8.0, 8.4) })[0]);     // 2.0 s before
        Assert.False(Merger.EchoMask(mic, new[] { W.Of("angebot", 7.9, 8.4) })[0]);    // 2.1 s before
        Assert.True(Merger.EchoMask(mic, new[] { W.Of("angebot", 10.99, 11.2) })[0]);  // < 1 s after
        Assert.False(Merger.EchoMask(mic, new[] { W.Of("angebot", 11.0, 11.2) })[0]);  // 1 s after
    }

    [Fact]
    public void LoneShortMatchIsKeptButTwoMatchesAreEcho()
    {
        Assert.False(Merger.EchoMask(new[] { W.Of("ja", 5, 5.2) }, new[] { W.Of("ja", 4.5, 4.7) })[0]);
        var mic = new[] { W.Of("ja", 5, 5.2), W.Of("gut", 5.3, 5.5) };
        var sys = new[] { W.Of("ja", 4.5, 4.7), W.Of("gut", 4.8, 5.0) };
        Assert.Equal(new[] { true, true }, Merger.EchoMask(mic, sys));
        // one long word matching alone is echo
        Assert.True(Merger.EchoMask(new[] { W.Of("Angebot", 5, 5.5) }, new[] { W.Of("angebot", 4.5, 5) })[0]);
    }

    [Fact]
    public void GarbledWordsBetweenMatchesAreEcho()
    {
        var sys = new[] { W.Of("heute", 1, 1.3), W.Of("morgen", 2, 2.3) };
        var mic = new[] { W.Of("heute", 1.2, 1.5), W.Of("xyz", 1.6, 1.8), W.Of("abc", 1.9, 2.0), W.Of("morgen", 2.2, 2.5) };
        Assert.Equal(new[] { true, true, true, true }, Merger.EchoMask(mic, sys));
        // four positions apart: the words between stay
        var mic2 = new[] { W.Of("heute", 1.2, 1.5), W.Of("x1", 1.6, 1.7), W.Of("x2", 1.7, 1.8), W.Of("x3", 1.8, 1.9), W.Of("morgen", 2.2, 2.5) };
        Assert.Equal(new[] { true, false, false, false, true }, Merger.EchoMask(mic2, sys));
    }

    [Fact]
    public void SimilarWordsByEditDistance()
    {
        Assert.True(Merger.Similar("stoppbild", "stockbild"));      // distance 2 ≤ 9/4
        Assert.False(Merger.Similar("abc", "abd"));                 // shorter than 4
        Assert.True(Merger.Similar("haus", "hau5"));                // distance 1 = max(1, 4/4)
        Assert.False(Merger.Similar("haus", "maul"));               // distance 2
        Assert.False(Merger.Similar("haus", "hausboot"));           // length difference > 1/4
    }

    [Fact]
    public void TurnsGroupPerSpeakerAndSortByStart()
    {
        var tagged = new List<(Word, string, Track)> { (W.Of("Hallo", 0, 0.5), "me", Track.Mic), (W.Of("zusammen", 0.6, 1), "me", Track.Mic),
            (W.Of("Hi", 0.8, 1.0), "s1", Track.System), (W.Of("Ja", 5, 5.2), "me", Track.Mic) };
        var t = Merger.Turns(tagged);
        Assert.Equal(new[] { "Hallo zusammen", "Hi", "Ja" }, t.Select(x => x.Text));
        Assert.Equal(new[] { 0, 1, 2 }, t.Select(x => x.Id));
    }

    [Fact]
    public void NewTurnAfterOneAndAHalfSeconds()
    {
        var tagged = new List<(Word, string, Track)> { (W.Of("a", 0, 1), "A", Track.Mic), (W.Of("b", 2.49, 3), "A", Track.Mic), (W.Of("c", 4.5, 5), "A", Track.System) };
        var t = Merger.Turns(tagged);
        Assert.Equal(new[] { "a b", "c" }, t.Select(x => x.Text));
        Assert.Equal(Track.System, t[1].Track);   // track of the first word
    }
}

public class SpeakerMathTests
{
    [Fact]
    public void MergesToTargetCount()
    {
        var c = new Dictionary<string, float[]> { ["a"] = new float[] { 1, 0, 0 }, ["b"] = new[] { 0.9f, 0.1f, 0 }, ["c"] = new float[] { 0, 0, 1 } };
        var m = SpeakerClusters.Merge(c, new Dictionary<string, double> { ["a"] = 10, ["b"] = 2, ["c"] = 5 }, 2, 2);
        Assert.Equal("a", m["b"]); Assert.Equal("a", m["a"]); Assert.Equal("c", m["c"]);
    }

    [Fact]
    public void AutoMergesOnlyVerySimilar()
    {
        var c = new Dictionary<string, float[]> { ["a"] = new float[] { 1, 0 }, ["b"] = new[] { 0.99f, 0.05f }, ["c"] = new float[] { 0, 1 } };
        var m = SpeakerClusters.Merge(c, new Dictionary<string, double>(), null, 0.9f);
        Assert.Equal(2, m.Values.Distinct().Count());
    }

    [Fact]
    public void AutoMergeThresholdIsInclusive()
    {
        var c = new Dictionary<string, float[]> { ["mic-0"] = new float[] { 1, 0 }, ["mic-1"] = new[] { 0.8f, 0.6f } };   // cosine 0.8
        Assert.Single(SpeakerClusters.Merge(c, new Dictionary<string, double>(), null, 0.80f).Values.Distinct());
        Assert.Equal(2, SpeakerClusters.Merge(c, new Dictionary<string, double>(), null, 0.81f).Values.Distinct().Count());
    }

    [Fact]
    public void MatchNeedsThresholdAndMargin()
    {
        var me = new float[] { 1, 0, 0 };
        var clear = new[] { ("a", new[] { 0.9f, 0.3f, 0 }), ("b", new[] { 0.2f, 1, 0f }) };
        Assert.Equal("a", SpeakerMatch.Best(me, clear, 0.5f, 0.08f, Array.Empty<float[]>()));
        var ambiguous = new[] { ("a", new[] { 0.9f, 0.3f, 0 }), ("b", new[] { 0.88f, 0.32f, 0 }) };
        Assert.Null(SpeakerMatch.Best(me, ambiguous, 0.5f, 0.08f, Array.Empty<float[]>()));
        var weak = new[] { ("a", new[] { 0.3f, 1f, 0 }) };
        Assert.Null(SpeakerMatch.Best(me, weak, 0.5f, 0.08f, Array.Empty<float[]>()));
    }

    [Fact]
    public void ReferenceVectorsRaiseTheRunnerUp()
    {
        var me = new float[] { 1, 0 };
        var cands = new[] { ("mic-0", new[] { 0.9f, 0.436f }) };
        Assert.Equal("mic-0", SpeakerMatch.Best(me, cands, 0.5f, 0.08f, new[] { cands[0].Item2 }));   // the winner itself is ignored
        Assert.Null(SpeakerMatch.Best(me, cands, 0.5f, 0.08f, new[] { new[] { 0.88f, 0.475f } }));   // a call voice almost as close
    }

    [Fact]
    public void LibraryLearnsRunningAverage()
    {
        var lib = new SpeakerLibrary();
        lib.Learn("Anna", new float[] { 1, 0 });
        lib.Learn("anna", new float[] { 0, 1 });
        Assert.Single(lib.Entries);
        Assert.Equal(2, lib.Entries[0].Count);
        Assert.Equal(1, SpeakerClusters.Cosine(lib.Entries[0].Embedding, new float[] { 1, 1 }), 5);
        lib.Learn("  ", new float[] { 1, 0 });
        Assert.Single(lib.Entries);
    }
}

public class SpeakerFixTests
{
    [Fact]
    public void ClustersOfDifferentTracksAreNeverMerged()
    {
        float[] a = { 1, 0, 0 }, b = { 0, 1, 0 };
        var c = new Dictionary<string, float[]> { ["mic-0"] = a, ["system-0"] = a, ["mic-1"] = b };
        var w = new Dictionary<string, double> { ["mic-0"] = 10, ["system-0"] = 10, ["mic-1"] = 5 };
        var auto = SpeakerClusters.Merge(c, w, null, 0.8f);
        Assert.Equal(new[] { "mic-0", "mic-1", "system-0" }, auto.Values.Distinct().OrderBy(x => x));
        var two = SpeakerClusters.Merge(c, w, 2, 0.8f);
        Assert.Equal("mic-0", two["mic-1"]); Assert.Equal("system-0", two["system-0"]);
        var one = SpeakerClusters.Merge(c, w, 1, 0.8f);
        Assert.Equal(new[] { "mic-0", "system-0" }, one.Values.Distinct().OrderBy(x => x));
    }

    [Fact]
    public void TrackWithoutSegmentsUsesItsOwnSpeakerId()
    {
        var r = new TrackResult(Track.System, new[] { W.Of("hallo", 0, 1) }, Array.Empty<SpeakerSegment>(), "system-0");
        Assert.Equal(new[] { "system-0" }, Merger.AssignSpeakers(r).Select(x => x.Speaker));
    }

    static Transcript Make(params (string Id, string Label, bool IsMe)[] labels) => new()
    {
        Turns = labels.Select((l, i) => new Turn { Id = i, Speaker = l.Id, Start = i * 10, End = i * 10 + 5, Text = "x", Track = Track.Mic }).ToList(),
        Speakers = labels.Select(l => new Speaker { Id = l.Id, Label = l.Label, Track = Track.Mic, IsMe = l.IsMe }).ToList(),
        Engine = "t",
    };

    [Fact]
    public void NewSpeakerGetsTheLowestUnusedNumber()
    {
        Assert.Equal("Speaker 3", Make(("a", "Speaker 2", false), ("b", "Erika Musterfrau", false), ("c", "Speaker 1", false)).UnusedSpeakerLabel());
        Assert.Equal("Speaker 1", Make(("a", "Speaker 2", false), ("b", "Speaker 3", false)).UnusedSpeakerLabel());
    }

    [Fact]
    public void ThisIsMeRelabelsThePreviousMeWithoutDuplicates()
    {
        var t = Make(("me", "Max Beispiel", true), ("a", "Speaker 1", false), ("b", "Speaker 2", false));
        t.MarkMe("b", "Max Beispiel");
        Assert.Equal(new[] { "Speaker 3", "Speaker 1", "Max Beispiel" }, t.Speakers.Select(s => s.Label));
        Assert.Equal(new[] { false, false, true }, t.Speakers.Select(s => s.IsMe));
        t.MarkMe("a", "  ");
        Assert.Equal("Me", t.Speakers.First(s => s.Id == "a").Label);
        Assert.Equal("Speaker 2", t.Speakers.First(s => s.Id == "b").Label);
    }

    [Fact]
    public void LiveLabelsUseTheNameAndSeparateCounters()
    {
        var call = new LiveSpeakerLabels(MeetingMode.Call, "Max Beispiel");
        Assert.Equal(new[] { "Max Beispiel", "Speaker 1", "Remote 1", "Remote 2", "Speaker 2", "Remote 1" },
            new[] { "me", "mic-1", "system-0", "system-1", "mic-2", "system-0" }.Select(id => call.Label(id)));
        var room = new LiveSpeakerLabels(MeetingMode.InPerson, "");
        Assert.Equal(new[] { "Me", "Speaker 1", "Speaker 2" }, new[] { "me", "mic-0", "mic-1" }.Select(id => room.Label(id)));
        var hybrid = new LiveSpeakerLabels(MeetingMode.Hybrid, "Erika");
        Assert.Equal(new[] { "Speaker 1", "Speaker 2", "…" }, new[] { "system-0", "mic-0", null }.Select(id => hybrid.Label(id)));
    }

    [Fact]
    public void ForgetIgnoresCase()
    {
        var lib = new SpeakerLibrary();
        lib.Learn("Erika Musterfrau", new float[] { 1, 0 });
        lib.Forget("erika MUSTERFRAU ");
        Assert.Empty(lib.Entries);
    }
}
