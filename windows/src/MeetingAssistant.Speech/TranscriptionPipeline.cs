using System.Diagnostics;
using MeetingAssistant.Core;
using MeetingAssistant.Core.Audio;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;

namespace MeetingAssistant.Speech;

/// Turns a recorded session into a speaker-labelled `transcript.json` (spec 01-recording,
/// "Offline transcription pipeline"). Steps 1–3 (audio, VAD, chunked ASR, diarization,
/// embeddings) run here on sherpa-onnx; steps 4–10 are `TranscriptAssembly.Build`.
public static class TranscriptionPipeline
{
    /// Speech collected per speaker for its embedding (s); stops once more than this.
    public const double EmbeddingMaxSeconds = 45;
    /// Minimum clean speech (s) for an embedding.
    public const double EmbeddingMinSeconds = 1;
    /// A segment overlapping other speakers by more than this share of its length is skipped.
    public const double EmbeddingMaxOverlap = 0.2;
    /// A track at or below this overall RMS is silent and skipped.
    public const float SilentRms = 1e-5f;

    /// Which tracks are diarized in a mode (Call/Hybrid: both; In person: mic).
    public static bool Diarizes(MeetingMode mode, Track t) => mode != MeetingMode.InPerson || t == Track.Mic;

    /// Run the pipeline for a session folder and write `transcript.json`.
    /// `progress` gets (fraction 0…1, message). `cleanup` drops invented credits and applies the
    /// vocabulary (Core/Text); null skips both.
    public static Transcript Run(string sessionId, PipelineSettings settings, ITranscriptTextCleanup? cleanup = null,
                                 Action<double, string>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var info = JsonIO.Read<SessionInfo>(AppPaths.Info(sessionId));
        var mode = info.Mode;
        var language = AsrEngineText.ParseLanguage(info.Language) ?? settings.Language;
        _ = language;   // Parakeet cannot be told the language; Windows has no Qwen3 language rescue.
        var engine = SpeechEngine.Shared;

        var tracks = info.Tracks.Where(t => File.Exists(AppPaths.TrackFile(sessionId, t))).ToList();
        if (tracks.Count == 0) throw PipelineException.NoAudio();

        var analyses = new List<TrackAnalysis>();
        for (int ti = 0; ti < tracks.Count; ti++)
        {
            var track = tracks[ti];
            double baseP = 0.05 + 0.9 * ti / tracks.Count, span = 0.9 / tracks.Count;
            var name = track == Track.Mic ? "microphone" : "call audio";
            progress?.Invoke(baseP, $"Reading {name}");
            var samples = AudioFile.Read(AppPaths.TrackFile(sessionId, track));
            if (SpeechEngine.Rms(samples) <= SilentRms) continue;
            AudioLevel.Apply(samples, AudioLevel.Gain(samples));   // Windows: boost quiet recordings

            ct.ThrowIfCancellationRequested();
            progress?.Invoke(baseP + span * 0.03, $"Detecting speech in {name}");
            var regions = SpeechEngine.SpeechRegions(samples, ct);
            double total = samples.Length / 16_000.0;
            // Windows runs Parakeet for every engine (no Qwen3), so Parakeet's chunk length.
            var chunks = Chunking.Chunks(regions, AsrEngine.ParakeetUltra.MaxChunkSeconds(), total);
            var words = new List<Word>();
            for (int ci = 0; ci < chunks.Count; ci++)
            {
                ct.ThrowIfCancellationRequested();
                var c = chunks[ci];
                progress?.Invoke(baseP + span * (0.05 + 0.75 * ci / Math.Max(1, chunks.Count)), $"Transcribing {name} ({ci + 1}/{chunks.Count})");
                words.AddRange(engine.Transcribe(SpeechEngine.Slice(samples, c.Start, c.End), c.Start));
            }

            List<SpeakerSegment>? segments = null;
            var centroids = new Dictionary<string, float[]>();
            var speech = new Dictionary<string, double>();
            if (Diarizes(mode, track) && words.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                double at = baseP + span * 0.82;
                progress?.Invoke(at, $"Identifying speakers in {name}");
                var diar = engine.Diarize(samples, regions);
                if (diar.Count > 0) segments = diar.Select(d => new SpeakerSegment($"{track.Raw()}-{d.Speaker}", d.Span)).ToList();
                foreach (var id in diar.Select(d => d.Speaker).Distinct().OrderBy(x => x))
                {
                    ct.ThrowIfCancellationRequested();
                    var own = diar.Where(d => d.Speaker == id).Select(d => d.Span).ToList();
                    var others = diar.Where(d => d.Speaker != id).Select(d => d.Span).ToList();
                    var audio = new List<float>();
                    foreach (var s in own.OrderByDescending(s => s.Duration))
                    {
                        if (others.Any(o => o.Overlap(s) > EmbeddingMaxOverlap * s.Duration)) continue;
                        audio.AddRange(SpeechEngine.Slice(samples, s.Start, s.End));
                        if (audio.Count > EmbeddingMaxSeconds * 16_000) break;
                    }
                    var key = $"{track.Raw()}-{id}";
                    speech[key] = own.Sum(s => s.Duration);
                    if (audio.Count >= EmbeddingMinSeconds * 16_000)
                    {
                        var e = engine.Embedding(audio.ToArray());
                        if (e.Length > 0) centroids[key] = e;
                    }
                }
            }
            analyses.Add(new TrackAnalysis { Track = track, Words = words, Segments = segments, Centroids = centroids, SpeechSeconds = speech });
            progress?.Invoke(baseP + span, $"Finished {name}");
        }

        var transcript = TranscriptAssembly.Build(analyses, mode, info.ExpectedSpeakers, settings,
                                                  VoiceprintStore.Load(), SpeakerLibraryStore.Load(), cleanup);
        transcript.Engine = AsrEngine.ParakeetUltra.Raw();
        transcript.CreatedAt = DateTime.UtcNow;
        transcript.ProcessingSeconds = sw.Elapsed.TotalSeconds;
        JsonIO.Write(transcript, AppPaths.TranscriptFile(sessionId));
        progress?.Invoke(1, "Done");
        return transcript;
    }
}
