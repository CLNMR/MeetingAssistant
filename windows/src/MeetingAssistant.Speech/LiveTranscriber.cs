using System.Diagnostics;
using System.Threading.Channels;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;

namespace MeetingAssistant.Speech;

/// Live transcription while recording and in the floating window (spec 01-recording, "Live
/// transcription"). Each track is cut into utterances by streaming Silero VAD (4096-sample
/// blocks, speech = p ≥ 0.5, end after 3 silent blocks or 14 s, 4096-sample pre-roll); every
/// finished utterance is transcribed by Parakeet and the open one is re-transcribed every 24 000
/// samples as a grey partial. Speakers come from `LiveDiarizer` (≈3 s behind). Lines are
/// published at most every 0.7 s from audio processing, plus on every finalized utterance.
/// Nothing is saved; the offline pipeline makes the real transcript.
public sealed class LiveTranscriber
{
    public const int Block = StreamingVad.BlockSize;
    public const int SilentBlocksToEnd = 3;
    public const int MaxUtteranceSamples = 14 * 16_000;
    public const int PartialEverySamples = 24_000;
    public static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(0.7);

    sealed class Stream
    {
        public required Track Track;
        public required StreamingVad Vad;
        public ILiveDiarizer? Diarizer;
        public readonly LiveGain Gain = new();
        public float[] Pending = Array.Empty<float>();
        public long Position;                 // samples consumed by VAD so far
        public float[] Preroll = Array.Empty<float>();
        public List<float> Utterance = new();
        public double UtteranceStart;
        public int SilentBlocks;
        public int LastPartialAt;
        public int Counter;
        public required Channel<float[]> Input;
        public Task? Worker;
    }

    readonly MeetingMode _mode;
    readonly Action<IReadOnlyList<LiveLine>> _onUpdate;
    readonly SpeechEngine _engine;
    readonly object _gate = new();
    readonly Dictionary<Track, Stream> _streams = new();
    readonly Dictionary<string, LiveUtterance> _utterances = new();
    readonly MeSlotTracker _me = new();
    readonly Stopwatch _sinceStart = Stopwatch.StartNew();
    TimeSpan _lastPublish = TimeSpan.FromDays(-1);
    IReadOnlyList<LiveLine> _lines = Array.Empty<LiveLine>();

    /// Creates the live diarizer of a track (window re-diarization by default; see `WindowDiarizer`).
    public static Func<SpeechEngine, ILiveDiarizer> DiarizerFactory { get; set; } = e => new WindowDiarizer(e.Embedding);

    /// `onUpdate` receives the full, sorted list of lines on every publish (any thread).
    public LiveTranscriber(MeetingMode mode, Action<IReadOnlyList<LiveLine>> onUpdate, SpeechEngine? engine = null)
    {
        _mode = mode;
        _onUpdate = onUpdate;
        _engine = engine ?? SpeechEngine.Shared;
    }

    /// The latest published lines.
    public IReadOnlyList<LiveLine> Lines { get { lock (_gate) return _lines; } }

    /// Diarized tracks: Call and Hybrid both, In person the mic.
    bool Diarizes(Track t) => _mode != MeetingMode.InPerson || t == Track.Mic;

    /// Create the per-track streams and load Parakeet. Feed audio after this.
    public void Start(IEnumerable<Track> tracks)
    {
        foreach (var t in tracks)
        {
            var s = new Stream
            {
                Track = t,
                Vad = new StreamingVad(),
                Input = Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions { SingleReader = true }),
            };
            if (Diarizes(t))
            {
                try { s.Diarizer = DiarizerFactory(_engine); }
                catch (Exception e) { Debug.WriteLine($"live diarizer unavailable: {e.Message}"); }
            }
            _streams[t] = s;
            s.Worker = Task.Run(async () =>
            {
                await foreach (var samples in s.Input.Reader.ReadAllAsync()) Process(s, samples);
            });
        }
        try { _engine.Preload(); } catch (Exception e) { Debug.WriteLine($"parakeet: {e.Message}"); }
    }

    /// Thread-safe entry point for capture callbacks: 16 kHz mono samples of one track.
    public void Feed(Track track, float[] samples)
    {
        if (_streams.TryGetValue(track, out var s)) s.Input.Writer.TryWrite(samples);
    }

    /// Finish: process what was fed, finalize open utterances, let the diarizer settle, publish.
    public async Task StopAsync()
    {
        foreach (var s in _streams.Values) s.Input.Writer.TryComplete();
        foreach (var s in _streams.Values) if (s.Worker is not null) await s.Worker;
        foreach (var s in _streams.Values)
        {
            FinalizeUtterance(s);
            s.Diarizer?.Finish();
            s.Vad.Dispose();
        }
        Publish();
        foreach (var s in _streams.Values) s.Diarizer?.Dispose();
    }

    // MARK: Processing (one worker per track)

    void Process(Stream s, float[] samples)
    {
        s.Gain.Observe(samples);
        var boosted = samples.ToArray();
        AudioLevel.Apply(boosted, s.Gain.Gain);
        s.Pending = s.Pending.Length == 0 ? boosted : [.. s.Pending, .. boosted];
        while (s.Pending.Length >= Block)
        {
            var block = s.Pending[..Block];
            s.Pending = s.Pending[Block..];
            double blockStart = s.Position / 16_000.0;
            s.Position += Block;
            bool speech;
            try { speech = s.Vad.IsSpeech(block); } catch { speech = false; }
            s.Diarizer?.Append(block, speech);   // thread-safe; heavy work outside the transcriber's lock

            if (s.Utterance.Count == 0)
            {
                if (speech)
                {
                    s.UtteranceStart = blockStart - s.Preroll.Length / 16_000.0;
                    s.Utterance = new List<float>(s.Preroll.Concat(block));
                    s.SilentBlocks = 0;
                    s.LastPartialAt = 0;
                }
                else
                {
                    var joined = s.Preroll.Concat(block).ToArray();
                    s.Preroll = joined[Math.Max(0, joined.Length - Block)..];
                }
                continue;
            }
            s.Utterance.AddRange(block);
            s.SilentBlocks = speech ? 0 : s.SilentBlocks + 1;
            if (s.SilentBlocks >= SilentBlocksToEnd || s.Utterance.Count >= MaxUtteranceSamples)
                FinalizeUtterance(s);
            else if (s.Utterance.Count - s.LastPartialAt >= PartialEverySamples)
            {
                s.LastPartialAt = s.Utterance.Count;
                TranscribeCurrent(s, final: false);
            }
        }
        // Speaker labels catch up continuously; refreshing ~1.5×/s is plenty for the eye.
        bool due;
        lock (_gate) due = _sinceStart.Elapsed - _lastPublish >= PublishInterval;
        if (due) Publish();
    }

    void FinalizeUtterance(Stream s)
    {
        if (s.Utterance.Count == 0) return;
        TranscribeCurrent(s, final: true);
        s.Utterance = new List<float>();
        s.Preroll = Array.Empty<float>();
        s.SilentBlocks = 0;
        s.Counter++;
        Publish();
    }

    void TranscribeCurrent(Stream s, bool final)
    {
        var id = $"{s.Track.Raw()}-{s.Counter}";
        try
        {
            var words = _engine.Transcribe(s.Utterance.ToArray(), s.UtteranceStart);
            lock (_gate)
            {
                if (words.Count == 0) { if (final) _utterances.Remove(id); return; }
                _utterances[id] = new LiveUtterance(id, s.Track, words, final);
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"live transcription failed: {e.Message}");
        }
    }

    // MARK: Speakers and lines

    /// Live id of a word: the track's fixed id without a diarizer (`me` for the Call mic),
    /// otherwise its slot (`me`/`mic-n` in Call mode for the mic, `<track>-<slot>` else), or null
    /// while the diarizer has not reached the word.
    string? SpeakerOf(Word w, Track t)
    {
        bool callMic = _mode == MeetingMode.Call && t == Track.Mic;
        var d = _streams.TryGetValue(t, out var s) ? s.Diarizer : null;
        if (d is null) return callMic ? "me" : $"{t.Raw()}-0";
        var slot = d.SlotFor(w);
        if (slot is null) return null;
        return callMic ? _me.MicSpeaker(slot) : $"{t.Raw()}-{slot}";
    }

    void Publish()
    {
        IReadOnlyList<LiveLine> lines;
        lock (_gate)
        {
            _lastPublish = _sinceStart.Elapsed;
            var utterances = _utterances.Values.ToList();
            var echo = _mode != MeetingMode.InPerson ? LiveLineBuilder.EchoWords(utterances) : new HashSet<Word>();
            if (_mode == MeetingMode.Call && _streams.TryGetValue(Track.Mic, out var mic) && mic.Diarizer is { } d)
            {
                var micWords = utterances.Where(u => u.Track == Track.Mic).SelectMany(u => u.Words).Where(w => !echo.Contains(w));
                _me.Update(micWords.Select(d.SlotFor));
            }
            lines = _lines = LiveLineBuilder.Build(utterances, SpeakerOf, echo);
        }
        try { _onUpdate(lines); } catch (Exception e) { Debug.WriteLine($"live update handler failed: {e.Message}"); }
    }
}
