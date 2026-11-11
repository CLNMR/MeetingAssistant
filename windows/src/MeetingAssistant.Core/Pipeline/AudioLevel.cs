namespace MeetingAssistant.Core.Pipeline;

/// Input gain for quiet recordings (Windows only; not in the Mac app). sherpa-onnx's Silero VAD
/// and Parakeet miss much of a quiet far-field room recording (a 32-min meeting with speech
/// around −40 dBFS lost 23 % of its words in the VAD alone), so audio is boosted before VAD, ASR
/// and diarization: the loud end of the speech level (90th percentile of 1-second RMS values)
/// is brought to `TargetLevel`, never attenuated, at most `MaxGain`×, and limited so that the
/// 99.9th-percentile sample stays below full scale.
public static class AudioLevel
{
    public const float TargetLevel = 0.1f;
    public const float MaxGain = 20f;
    const int Second = 16_000;

    /// Gain for a whole track (1 when the audio is loud enough or silent).
    public static float Gain(ReadOnlySpan<float> samples)
    {
        int seconds = samples.Length / Second;
        if (seconds == 0) return GainFor(Rms(samples), PeakPercentile(samples));
        var levels = new float[seconds];
        for (int i = 0; i < seconds; i++) levels[i] = Rms(samples.Slice(i * Second, Second));
        Array.Sort(levels);
        return GainFor(levels[(int)(seconds * 0.9)], PeakPercentile(samples));
    }

    /// Gain from a speech level and the 99.9th-percentile absolute sample.
    public static float GainFor(float level, float peak)
    {
        if (level <= 1e-5f) return 1;
        float g = Math.Min(TargetLevel / level, MaxGain);
        if (peak > 0) g = Math.Min(g, 0.99f / peak);
        return Math.Max(1, g);
    }

    /// Apply a gain in place (clamped to ±1).
    public static void Apply(Span<float> samples, float gain)
    {
        if (gain == 1) return;
        for (int i = 0; i < samples.Length; i++) samples[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
    }

    public static float Rms(ReadOnlySpan<float> s)
    {
        if (s.Length == 0) return 0;
        double sum = 0;
        foreach (var x in s) sum += x * x;
        return (float)Math.Sqrt(sum / s.Length);
    }

    /// 99.9th percentile of |sample| (on every 4th sample, for speed).
    static float PeakPercentile(ReadOnlySpan<float> s)
    {
        if (s.Length == 0) return 0;
        var abs = new float[(s.Length + 3) / 4];
        for (int i = 0, j = 0; i < s.Length; i += 4, j++) abs[j] = Math.Abs(s[i]);
        Array.Sort(abs);
        return abs[Math.Min(abs.Length - 1, (int)(abs.Length * 0.999))];
    }
}

/// Running input gain for live audio: `AudioLevel.GainFor` over the last minute of 1-second
/// RMS values (no peak limit; samples are clamped to ±1).
public sealed class LiveGain
{
    readonly Queue<float> _levels = new();
    double _sum;
    int _n;
    const int History = 60;

    /// Current gain (1 until a second of audio was seen).
    public float Gain { get; private set; } = 1;

    /// Feed raw samples (call before applying `Gain` to them).
    public void Observe(ReadOnlySpan<float> samples)
    {
        foreach (var x in samples)
        {
            _sum += x * x;
            if (++_n < 16_000) continue;
            _levels.Enqueue((float)Math.Sqrt(_sum / _n));
            if (_levels.Count > History) _levels.Dequeue();
            _sum = 0; _n = 0;
            var sorted = _levels.OrderBy(v => v).ToList();
            Gain = AudioLevel.GainFor(sorted[(int)(sorted.Count * 0.9)], 0);
        }
    }
}
