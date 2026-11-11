using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;
using SherpaOnnx;

namespace MeetingAssistant.Speech;

/// A streaming diarizer for one live track: fed 4096-sample blocks with their VAD decision, it
/// exposes weights per 10 ms frame for 8 slots (`frame * 8 + slot`) and how many frames are final.
/// `Append`/`Finish` are called from one worker thread; `SlotFor` may be called from any thread.
public interface ILiveDiarizer : IDisposable
{
    void Append(float[] block, bool isSpeech);
    void Finish();
    /// The word's slot (`LiveSlots.SlotFor` over the frame weights), or null while lagging.
    int? SlotFor(Word w);
}

/// Live speaker separation by re-running offline diarization on a sliding window (spec
/// 01-recording, "Live diarization"; Windows replacement for the Mac's streaming Nemotron 3).
/// Every `HopSeconds` the last `WindowSeconds` of the track are diarized (pyannote segmentation +
/// CAM++ clustering). Each local speaker with enough speech in the window gets an embedding and
/// is matched to the global slots by cosine (greedy, best pair first; at least `MatchThreshold`),
/// otherwise it opens a new slot (up to 8). Frames between the settled point and
/// `window end − MarginSeconds` are then written once as one-hot slot weights, so a word's label
/// never changes after it appears; words later than that are "lagging" (null), ≈ hop + margin +
/// processing behind (≈ 4–5 s). Chosen over online clustering of 3 s CAM++ windows, which on a
/// 25-minute room recording was barely better than one speaker for everyone (word purity 0.51
/// vs 0.48); this gave 0.60 purity / 0.73 coverage against the Mac's speakers, at ≈15 % CPU of
/// real time per track. Same output as Nemotron: weights per 10 ms frame for 8 slots.
public sealed class WindowDiarizer : ILiveDiarizer
{
    public static double WindowSeconds { get; set; } = 20;
    public static double HopSeconds { get; set; } = 3;
    public static double MarginSeconds { get; set; } = 1;
    /// A local speaker joins a slot whose centroid is at least this similar.
    public static float MatchThreshold { get; set; } = 0.5f;
    /// A local speaker needs this much speech (s) in the window to get an embedding.
    public static double MinSpeakerSeconds { get; set; } = 1.0;
    /// Clustering threshold inside a window: lower than the offline one, because splitting one
    /// voice into two local speakers is harmless (both match the same slot) while merging two
    /// voices is not.
    public static float LocalClusterThreshold { get; set; } = 0.6f;
    const int Rate = 16_000;

    readonly OfflineSpeakerDiarization _diarizer;
    readonly Func<float[], float[]> _embed;
    readonly List<float> _audio = new();            // the last WindowSeconds of samples
    long _audioStart;                                // sample index of _audio[0]
    long _total;                                     // samples appended
    long _sinceRun;
    readonly List<float> _probs = new();
    readonly float[]?[] _centroids = new float[LiveSlots.SlotCount][];
    int _settledFrames;

    public WindowDiarizer(Func<float[], float[]> embed, OfflineSpeakerDiarization? diarizer = null)
    {
        _embed = embed;
        _diarizer = diarizer ?? SpeechEngine.CreateDiarizer(LocalClusterThreshold);
    }

    readonly object _sync = new();

    public int? SlotFor(Word w) { lock (_sync) return LiveSlots.SlotFor(w, _probs, _settledFrames); }

    /// Frames (10 ms) whose slot weights are final.
    public int CoveredFrames => _settledFrames;
    public IReadOnlyList<float> Probabilities => _probs;
    public int SlotCount => _centroids.Count(c => c is not null);

    /// Add a block (the VAD decision is not needed: segmentation finds speech itself).
    public void Append(float[] block, bool isSpeech) => Append(block);

    /// Add samples (16 kHz, already gain-adjusted); runs a window every hop.
    public void Append(float[] samples)
    {
        _audio.AddRange(samples);
        _total += samples.Length;
        int frames = (int)(_total / 160);
        lock (_sync) while (_probs.Count < frames * LiveSlots.SlotCount) _probs.Add(0);
        int keep = (int)(WindowSeconds * Rate);
        if (_audio.Count > keep) { int drop = _audio.Count - keep; _audio.RemoveRange(0, drop); _audioStart += drop; }
        _sinceRun += samples.Length;
        if (_sinceRun >= HopSeconds * Rate && _audio.Count >= 2 * Rate)
        {
            _sinceRun = 0;
            Run(final: false);
        }
    }

    /// End of stream: diarize what is left and settle every frame.
    public void Finish()
    {
        if (_audio.Count >= Rate / 2) Run(final: true);
        lock (_sync) _settledFrames = (int)(_total / 160);
    }

    void Run(bool final)
    {
        var window = _audio.ToArray();
        double offset = _audioStart / (double)Rate;
        var segs = _diarizer.Process(window);
        var bySpeaker = segs.GroupBy(s => s.Speaker).ToList();

        // Embedding per local speaker from its segments in this window.
        var local = new List<(int Speaker, float[] Embedding, double Seconds)>();
        foreach (var g in bySpeaker)
        {
            double secs = g.Sum(s => s.End - s.Start);
            if (secs < MinSpeakerSeconds) continue;
            var audio = new List<float>();
            foreach (var s in g.OrderByDescending(s => s.End - s.Start))
            {
                int a = Math.Max(0, (int)(s.Start * Rate)), b = Math.Min(window.Length, (int)(s.End * Rate));
                if (b > a) audio.AddRange(window.AsSpan(a, b - a).ToArray());
                if (audio.Count > 10 * Rate) break;
            }
            var e = _embed(audio.ToArray());
            if (e.Length > 0) local.Add((g.Key, e, secs));
        }

        // Greedy matching, best pair first; each slot takes at most one local speaker.
        var map = new Dictionary<int, int>();
        var pairs = new List<(int Speaker, int Slot, float Score)>();
        foreach (var l in local)
            for (int k = 0; k < _centroids.Length; k++)
                if (_centroids[k] is { } c) pairs.Add((l.Speaker, k, SpeakerClusters.Cosine(c, l.Embedding)));
        foreach (var p in pairs.OrderByDescending(p => p.Score))
        {
            if (p.Score < MatchThreshold) break;
            if (map.ContainsKey(p.Speaker) || map.ContainsValue(p.Slot)) continue;
            map[p.Speaker] = p.Slot;
        }
        foreach (var l in local.OrderByDescending(l => l.Seconds))
        {
            if (!map.TryGetValue(l.Speaker, out var slot))
            {
                slot = NewSlot(l.Embedding);
                map[l.Speaker] = slot;
            }
            else Add(slot, l.Embedding);
        }

        // Settle frames up to the window end minus the margin (all of it at the end).
        int endFrame = (int)(_total / 160) - (final ? 0 : (int)(MarginSeconds * 100));
        int from = Math.Max(_settledFrames, (int)(_audioStart / 160));
        if (endFrame <= from) return;
        lock (_sync)
        {
            foreach (var s in segs)
            {
                if (!map.TryGetValue(s.Speaker, out var slot)) continue;
                int a = Math.Max(from, (int)((offset + s.Start) * 100)), b = Math.Min(endFrame, (int)((offset + s.End) * 100));
                for (int f = a; f < b; f++) _probs[f * LiveSlots.SlotCount + slot] += 1;
            }
            _settledFrames = endFrame;
        }
    }

    int NewSlot(float[] e)
    {
        for (int k = 0; k < _centroids.Length; k++)
            if (_centroids[k] is null) { _centroids[k] = e.ToArray(); return k; }
        int best = 0; float score = -2;
        for (int k = 0; k < _centroids.Length; k++)
        {
            var c = SpeakerClusters.Cosine(_centroids[k]!, e);
            if (c > score) { score = c; best = k; }
        }
        Add(best, e);
        return best;
    }

    void Add(int slot, float[] e)
    {
        var c = _centroids[slot]!;
        for (int i = 0; i < c.Length && i < e.Length; i++) c[i] += e[i];
    }

    public void Dispose() => _diarizer.Dispose();
}
