using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;
using MeetingAssistant.Speech;
using Xunit;

namespace MeetingAssistant.Core.Tests.Speech;

public class TokenWordsTests
{
    [Fact]
    public void JoinsPiecesAndPunctuationIntoWords()
    {
        // Parakeet tokens as sherpa-onnx returns them: a leading space starts a word.
        var tokens = new[] { " Al", "les", " hat", " ein", " En", "de", ",", " nur" };
        var starts = new float[] { 0, 0.24f, 0.4f, 0.64f, 0.8f, 0.96f, 1.04f, 1.2f };
        var durs = new float[] { 0.24f, 0.16f, 0.24f, 0.16f, 0.16f, 0.08f, 0.16f, 0.24f };
        var w = TokenWords.Build(tokens, starts, durs, 3);
        Assert.Equal(new[] { "Alles", "hat", "ein", "Ende,", "nur" }, w.Select(x => x.W));
        Assert.Equal(0.4, w[0].End, 5);
        Assert.Equal(1.2, w[3].End, 5);
        Assert.Equal(1.44, w[4].End, 5);
    }

    [Fact]
    public void SentencePieceMarkerAndNoDurations()
    {
        var w = TokenWords.Build(new[] { "▁Max", "▁Bei", "spiel" }, new float[] { 0, 0.5f, 0.7f }, null, 1.0);
        Assert.Equal(new[] { "Max", "Beispiel" }, w.Select(x => x.W));
        Assert.Equal(0.5, w[0].End, 5);          // up to the next token
        Assert.Equal(0.78, w[1].End, 5);         // last token + 0.08 s
    }

    [Fact]
    public void EndIsCappedAtTheClip()
    {
        var w = TokenWords.Build(new[] { " ja" }, new float[] { 0.95f }, new float[] { 0.3f }, 1.0);
        Assert.Equal(1.0, w[0].End, 5);
    }
}

public class QueueTests
{
    sealed class Recorder
    {
        public readonly List<(string, SessionStatus)> Statuses = new();
        public void Write(string id, SessionStatus s) { lock (Statuses) Statuses.Add((id, s)); }
    }

    static Transcript Empty() => new() { Engine = "parakeet-ultra" };

    [Fact]
    public async Task RunsInOrderOnceAndUsesTheOneOffEngineOnlyOnce()
    {
        var rec = new Recorder();
        var engines = new List<(string, AsrEngine)>();
        var gate = new ManualResetEventSlim(false);
        var q = new TranscriptionQueue(() => new PipelineSettings(), null, (id, s, _, p, _) =>
        {
            gate.Wait(5000);
            lock (engines) engines.Add((id, s.Engine));
            p(0.5, "half");
            return Empty();
        }) { WriteStatus = rec.Write, UnloadModels = () => { } };
        int unloads = 0;
        q.Idle += () => Interlocked.Increment(ref unloads);
        q.Enqueue("a");
        q.Enqueue("b");
        q.Enqueue("b");                       // never queued twice
        q.Enqueue("a");                       // running or waiting: ignored
        gate.Set();
        await q.WhenIdle();
        Assert.Equal(new[] { ("a", AsrEngine.ParakeetUltra), ("b", AsrEngine.ParakeetUltra) }, engines);
        Assert.Equal(new[] { SessionStatus.Queued, SessionStatus.Transcribing, SessionStatus.Done },
            rec.Statuses.Where(x => x.Item1 == "b").Select(x => x.Item2));
        Assert.Equal(1, unloads);
    }

    [Fact]
    public async Task FailureMarksFailedAndContinues()
    {
        var rec = new Recorder();
        var q = new TranscriptionQueue(() => new PipelineSettings(), null, (id, _, _, _, _) =>
            id == "bad" ? throw new InvalidOperationException("boom") : Empty()) { WriteStatus = rec.Write, UnloadModels = () => { } };
        Exception? failure = null;
        q.Failed += (_, e) => failure = e;
        q.Enqueue("bad");
        q.Enqueue("good");
        await q.WhenIdle();
        Assert.Equal("boom", failure?.Message);
        Assert.Contains(("bad", SessionStatus.Failed), rec.Statuses);
        Assert.Contains(("good", SessionStatus.Done), rec.Statuses);
    }

    [Fact]
    public async Task CancelEmptiesTheQueueAndResetsToRecorded()
    {
        var rec = new Recorder();
        var started = new ManualResetEventSlim(false);
        var q = new TranscriptionQueue(() => new PipelineSettings(), null, (_, _, _, _, ct) =>
        {
            started.Set();
            while (true) { ct.ThrowIfCancellationRequested(); Thread.Sleep(5); }
        }) { WriteStatus = rec.Write, UnloadModels = () => { } };
        q.Enqueue("a");
        q.Enqueue("b");
        Assert.True(started.Wait(5000));
        q.Cancel();
        await q.WhenIdle();
        Assert.Equal(SessionStatus.Recorded, rec.Statuses.Last(x => x.Item1 == "a").Item2);
        Assert.Equal(SessionStatus.Recorded, rec.Statuses.Last(x => x.Item1 == "b").Item2);
        Assert.DoesNotContain(("b", SessionStatus.Transcribing), rec.Statuses);
    }
}

/// Tests that need the downloaded models; they pass trivially (skip) when the models are absent.
public class ModelTests
{
    static bool Ready => ModelCatalog.CoreModelsReady();

    [Fact]
    public void CatalogPointsAtSherpaOnnxReleases()
    {
        Assert.Equal(4, ModelCatalog.Steps.Count);
        Assert.All(ModelCatalog.Steps, s => Assert.StartsWith("https://github.com/k2-fsa/sherpa-onnx/releases/download/", s.Model.Url));
        Assert.Equal(new[] { 0.02, 0.08, 0.2, 0.3 }, ModelCatalog.Steps.Select(s => s.From));
        Assert.Equal(new[] { "silero-vad", "pyannote-segmentation-3-0", "campplus", "parakeet-ultra" }, ModelCatalog.Steps.Select(s => s.Model.Folder));
    }

    [Fact]
    public void DictationReturnsEmptyForSilenceAndShortClips()
    {
        // These checks run before any model is touched.
        Assert.Equal("", SpeechEngine.Shared.Dictate(new float[3_999]));
        Assert.Equal("", SpeechEngine.Shared.Dictate(new float[16_000]));
    }

    [Fact]
    public void SpeechRegionsAndEmbeddingsWhenModelsExist()
    {
        if (!Ready) return;   // models not downloaded on this machine
        var tone = Enumerable.Range(0, 32_000).Select(i => (float)(0.3 * Math.Sin(i * 0.07))).ToArray();
        Assert.NotNull(SpeechEngine.SpeechRegions(new float[32_000]));
        Assert.Empty(SpeechEngine.SpeechRegions(new float[32_000]));
        var e = SpeechEngine.Shared.Embedding(tone);
        Assert.Equal(192, e.Length);
        Assert.Equal(1, Math.Sqrt(e.Sum(x => (double)x * x)), 3);
        Assert.Empty(SpeechEngine.Shared.Transcribe(new float[1_600], 0));
    }
}
