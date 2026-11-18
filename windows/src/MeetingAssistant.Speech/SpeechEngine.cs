using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;
using SherpaOnnx;

namespace MeetingAssistant.Speech;

/// Owns the loaded sherpa-onnx models so each is loaded once per app run (the Mac's
/// `ModelStore`). Each model has its own lock, so live transcription keeps running while the
/// final pipeline diarizes a long track.
public sealed class SpeechEngine : IDisposable
{
    public static SpeechEngine Shared { get; } = new();

    readonly object _asrLock = new(), _embLock = new(), _diarLock = new(), _loadLock = new();
    OfflineRecognizer? _parakeet;
    SpeakerEmbeddingExtractor? _embedder;
    OfflineSpeakerDiarization? _diarizer;

    /// Threads per model (sherpa-onnx CPU inference).
    public static int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// Decides the provider (CPU or DirectML) of Parakeet, CAM++ and the diarizers right before one of
    /// them loads (the app: setting + graphics-card check, see <see cref="SpeechAcceleration"/>).
    /// Null = CPU (msctl, tests). Silero VAD always runs on the CPU.
    public static Func<SpeechProviderChoice>? ProviderSource { get; set; }

    readonly SpeechProviderChoice? _provider;

    /// An engine whose models use <paramref name="provider"/> (the graphics-card check); by default
    /// each model asks <see cref="ProviderSource"/> when it loads.
    public SpeechEngine(SpeechProviderChoice? provider = null) => _provider = provider;

    static SpeechProviderChoice CurrentProvider() => ProviderSource?.Invoke() ?? SpeechProviderChoice.Cpu();

    SpeechProviderChoice Provider() => _provider ?? CurrentProvider();

    /// Distance threshold of sherpa-onnx's agglomerative clustering in offline diarization.
    /// Lower = more clusters; the pipeline then merges same-voice clusters (cosine ≥ 0.80).
    public static float DiarizationClusterThreshold { get; set; } = 1.1f;

    static void Require(ModelSpec m)
    {
        if (!m.IsReady) throw new InvalidOperationException("Download the speech models first (main window).");
    }

    OfflineRecognizer Parakeet()
    {
        lock (_loadLock)
        {
            if (_parakeet is not null) return _parakeet;
            var m = ModelCatalog.Parakeet;
            Require(m);
            var p = Provider();
            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = 16_000;
            config.FeatConfig.FeatureDim = 80;   // sherpa-onnx reads the real value from the NeMo model
            config.ModelConfig.Transducer.Encoder = m.File("encoder.int8.onnx");
            config.ModelConfig.Transducer.Decoder = m.File("decoder.int8.onnx");
            config.ModelConfig.Transducer.Joiner = m.File("joiner.int8.onnx");
            config.ModelConfig.Tokens = m.File("tokens.txt");
            config.ModelConfig.ModelType = "nemo_transducer";
            config.ModelConfig.NumThreads = p.NumThreads;
            config.ModelConfig.Provider = p.Provider;
            config.DecodingMethod = "greedy_search";
            return _parakeet = new OfflineRecognizer(config);
        }
    }

    SpeakerEmbeddingExtractor Embedder()
    {
        lock (_loadLock)
        {
            if (_embedder is not null) return _embedder;
            Require(ModelCatalog.CampPlus);
            var p = Provider();
            var c = new SpeakerEmbeddingExtractorConfig
            {
                Model = ModelCatalog.CampPlus.File("campplus.onnx"), NumThreads = p.NumThreads, Provider = p.Provider,
            };
            return _embedder = new SpeakerEmbeddingExtractor(c);
        }
    }

    OfflineSpeakerDiarization Diarizer()
    {
        lock (_loadLock) return _diarizer ??= CreateDiarizer(provider: _provider);
    }

    /// A new offline diarizer: pyannote segmentation 3.0 + CAM++ with agglomerative clustering
    /// (`DiarizationClusterThreshold`), minimum segment 0.25 s. The live window diarizer owns its own.
    /// Segmentation and embedding use <paramref name="provider"/>, by default <see cref="ProviderSource"/>.
    public static OfflineSpeakerDiarization CreateDiarizer(float? clusterThreshold = null, SpeechProviderChoice? provider = null)
    {
        Require(ModelCatalog.Segmentation);
        Require(ModelCatalog.CampPlus);
        var p = provider ?? CurrentProvider();
        var c = new OfflineSpeakerDiarizationConfig();
        c.Segmentation.Pyannote.Model = ModelCatalog.Segmentation.File("model.onnx");
        c.Segmentation.NumThreads = p.NumThreads;
        c.Segmentation.Provider = p.Provider;
        c.Embedding.Model = ModelCatalog.CampPlus.File("campplus.onnx");
        c.Embedding.NumThreads = p.NumThreads;
        c.Embedding.Provider = p.Provider;
        c.Clustering.NumClusters = -1;
        c.Clustering.Threshold = clusterThreshold ?? DiarizationClusterThreshold;
        c.MinDurationOn = 0.25f;
        c.MinDurationOff = 0f;
        return new OfflineSpeakerDiarization(c);
    }

    /// Load Parakeet now (app start with dictation on, or before live transcription).
    public void Preload() => Parakeet();

    /// Free the final-pass diarizer (queue empty). Parakeet and CAM++ stay for live use.
    public void UnloadTranscriptionModels()
    {
        lock (_diarLock) lock (_loadLock) { _diarizer?.Dispose(); _diarizer = null; }
    }

    // MARK: VAD

    /// Silero VAD parameters of the offline pipeline (spec 01-recording, step 3.2).
    public const float VadThreshold = 0.5f;
    public const float MinSpeech = 0.2f, MinSilence = 0.5f, MaxSpeech = 60f, SpeechPadding = 0.1f;

    /// A new Silero VAD instance (each caller owns its own; they are not shared).
    public static VoiceActivityDetector CreateVad(float minSpeech, float minSilence, float maxSpeech, float bufferSeconds = 120, float threshold = VadThreshold)
    {
        Require(ModelCatalog.SileroVad);
        var c = new VadModelConfig();
        c.SileroVad.Model = ModelCatalog.SileroVad.File("silero_vad.onnx");
        c.SileroVad.Threshold = threshold;
        c.SileroVad.MinSpeechDuration = minSpeech;
        c.SileroVad.MinSilenceDuration = minSilence;
        c.SileroVad.MaxSpeechDuration = maxSpeech;
        c.SileroVad.WindowSize = 512;
        c.SampleRate = 16_000;
        c.NumThreads = 1;
        c.Provider = "cpu";
        return new VoiceActivityDetector(c, bufferSeconds);
    }

    /// Speech regions (s) of a whole track: Silero, threshold 0.5, min speech 0.2 s, min silence
    /// 0.5 s, max speech 60 s, padding 0.1 s on each side (clamped to the audio).
    public static List<TimeRange> SpeechRegions(float[] samples, CancellationToken ct = default, float threshold = VadThreshold)
    {
        using var vad = CreateVad(MinSpeech, MinSilence, MaxSpeech, samples.Length / 16_000f + 30, threshold);
        var raw = new List<TimeRange>();
        void Drain()
        {
            while (!vad.IsEmpty())
            {
                var seg = vad.Front();
                double start = seg.Start / 16_000.0;
                raw.Add(new TimeRange(start, start + seg.Samples.Length / 16_000.0));
                vad.Pop();
            }
        }
        const int block = 512 * 64;
        for (int i = 0; i < samples.Length; i += block)
        {
            ct.ThrowIfCancellationRequested();
            vad.AcceptWaveform(samples.AsSpan(i, Math.Min(block, samples.Length - i)).ToArray());
            Drain();
        }
        vad.Flush();
        Drain();
        double total = samples.Length / 16_000.0;
        return raw.Select(r => new TimeRange(Math.Max(0, r.Start - SpeechPadding), Math.Min(total, r.End + SpeechPadding))).ToList();
    }

    // MARK: ASR

    /// Raw Parakeet result: text and per-token start times (s, relative to the clip).
    public (string Text, string[] Tokens, float[] Timestamps, float[]? Durations) Recognize(float[] audio)
    {
        var rec = Parakeet();
        lock (_asrLock)
        {
            using var stream = rec.CreateStream();
            stream.AcceptWaveform(16_000, audio);
            rec.Decode(stream);
            var r = stream.Result;
            return (r.Text ?? "", r.Tokens ?? Array.Empty<string>(), r.Timestamps ?? Array.Empty<float>(), r.Durations);
        }
    }

    /// Transcribe one chunk with Parakeet; words carry absolute times (offset + token time).
    /// Clips of ≤ 1600 samples give no words. Without token timings the words are spread evenly.
    /// (Windows has no Qwen3 and no language rescue; spec 01 "ASR engines".)
    public List<Word> Transcribe(float[] audio, double offset)
    {
        if (audio.Length <= 1600) return new();
        var r = Recognize(audio);
        double end = audio.Length / 16_000.0;
        var words = TokenWords.Build(r.Tokens, r.Timestamps, r.Durations, end);
        if (words.Count == 0 && !string.IsNullOrWhiteSpace(r.Text))
            return WordAlignment.Spread(r.Text.Trim(), offset, offset + end);
        return words.Select(w => new Word(w.W, w.Start + offset, w.End + offset)).ToList();
    }

    /// Push-to-talk dictation (spec 04 §1.5): "" below 4000 samples or RMS ≤ 1e-4; one span up
    /// to 14 s, else VAD regions chunked to ≤ 14 s; spans ≤ 1600 samples skipped, shorter than
    /// 24 000 samples padded with silence; texts trimmed and joined with single spaces.
    public string Dictate(float[] samples)
    {
        if (samples.Length < 4_000 || Rms(samples) <= 1e-4) return "";
        double total = samples.Length / 16_000.0;
        double window = AsrEngine.ParakeetUltra.MaxChunkSeconds();
        var spans = total <= window
            ? new List<TimeRange> { new(0, total) }
            : Chunking.Chunks(SpeechRegions(samples), window, total);
        var parts = new List<string>();
        foreach (var s in spans)
        {
            var audio = Slice(samples, s.Start, s.End);
            if (audio.Length <= 1_600) continue;
            if (audio.Length < 24_000) Array.Resize(ref audio, 24_000);
            var text = Recognize(audio).Text.Trim();
            if (text.Length > 0) parts.Add(text);
        }
        return string.Join(" ", parts);
    }

    // MARK: Speakers

    /// 192-d L2-normalised CAM++ embedding of a clip.
    public float[] Embedding(float[] audio)
    {
        var e = Embedder();
        lock (_embLock)
        {
            using var stream = e.CreateStream();
            stream.AcceptWaveform(16_000, audio);
            stream.InputFinished();
            if (!e.IsReady(stream)) return Array.Empty<float>();
            return SpeakerClusters.Normalize(e.Compute(stream));
        }
    }

    /// Offline diarization of a whole track (pyannote segmentation 3.0 + CAM++ clustering).
    /// Audio outside the speech regions ±1 s is zeroed first (the Mac passes the same speech mask
    /// to Nemotron). Speaker indices are as returned by sherpa-onnx.
    public List<(int Speaker, TimeRange Span)> Diarize(float[] samples, IReadOnlyList<TimeRange> speech,
                                                       Action<double>? progress = null)
    {
        var masked = new float[samples.Length];
        foreach (var r in speech)
        {
            int a = Math.Max(0, (int)((r.Start - 1) * 16_000)), b = Math.Min(samples.Length, (int)((r.End + 1) * 16_000));
            if (a < b) Array.Copy(samples, a, masked, a, b - a);
        }
        var d = Diarizer();
        lock (_diarLock)
        {
            OfflineSpeakerDiarizationSegment[] segs;
            if (progress is null) segs = d.Process(masked);
            else
            {
                OfflineSpeakerDiarizationProgressCallback cb = (done, total, _) =>
                {
                    if (total > 0) progress((double)done / total);
                    return 0;
                };
                segs = d.ProcessWithCallback(masked, cb, IntPtr.Zero);
                GC.KeepAlive(cb);
            }
            return segs.Select(s => (s.Speaker, new TimeRange(s.Start, s.End))).ToList();
        }
    }

    // MARK: Graphics-card check

    /// The child process of the graphics-card check (`-gpuProbe`, see <see cref="SpeechAcceleration"/>):
    /// loads CAM++, pyannote segmentation and Parakeet with DirectML, runs each once on 2 s of
    /// synthetic speech-like audio and prints <see cref="SpeechAcceleration.OkMarker"/>. sherpa-onnx
    /// reports a provider it could not use on stderr; the parent reads that. Returns the exit code.
    public static int RunGpuProbe(TextWriter output, TextWriter error)
    {
        try
        {
            foreach (var m in new[] { ModelCatalog.CampPlus, ModelCatalog.Segmentation, ModelCatalog.Parakeet }) Require(m);
            var audio = ProbeAudio(2.0);
            using var engine = new SpeechEngine(SpeechProviderChoice.Gpu());
            if (engine.Embedding(audio).Length == 0) { error.WriteLine("gpu probe: no embedding"); return 2; }
            using (var d = CreateDiarizer(provider: SpeechProviderChoice.Gpu())) d.Process(audio);
            engine.Recognize(audio);
            // sherpa-onnx falls back to the CPU silently when DirectML cannot be set up (only a line
            // on stderr, which a windowless process may not deliver), so also require that ONNX
            // Runtime actually loaded DirectML.dll into this process.
            if (OperatingSystem.IsWindows() && !System.Diagnostics.Process.GetCurrentProcess().Modules
                    .Cast<System.Diagnostics.ProcessModule>()
                    .Any(m => string.Equals(m.ModuleName, "DirectML.dll", StringComparison.OrdinalIgnoreCase)))
            {
                error.WriteLine("gpu probe: DirectML.dll was not loaded (Fallback to cpu)");
                error.Flush();
                return 3;
            }
            output.WriteLine(SpeechAcceleration.OkMarker);
            output.Flush();
            return 0;
        }
        catch (Exception e)
        {
            error.WriteLine($"gpu probe: {e.GetType().Name}: {e.Message}");
            error.Flush();
            return 1;
        }
    }

    /// Speech-like test audio: a 120 Hz voice with harmonics, syllable-rate amplitude changes.
    internal static float[] ProbeAudio(double seconds)
    {
        var a = new float[(int)(seconds * 16_000)];
        for (int i = 0; i < a.Length; i++)
        {
            double t = i / 16_000.0;
            double f0 = 120 + 20 * Math.Sin(2 * Math.PI * 0.7 * t);
            double voice = 0;
            for (int h = 1; h <= 8; h++) voice += Math.Sin(2 * Math.PI * f0 * h * t) / h;
            double envelope = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 4 * t);
            a[i] = (float)(0.08 * voice * envelope);
        }
        return a;
    }

    // MARK: Helpers

    public static float[] Slice(float[] s, double start, double end)
    {
        int a = Math.Max(0, Math.Min(s.Length, (int)(start * 16_000)));
        int b = Math.Max(a, Math.Min(s.Length, (int)(end * 16_000)));
        return s.AsSpan(a, b - a).ToArray();
    }

    public static float Rms(ReadOnlySpan<float> s)
    {
        if (s.Length == 0) return 0;
        double sum = 0;
        foreach (var x in s) sum += x * x;
        return (float)Math.Sqrt(sum / s.Length);
    }

    public void Dispose()
    {
        _parakeet?.Dispose();
        _embedder?.Dispose();
        _diarizer?.Dispose();
    }
}
