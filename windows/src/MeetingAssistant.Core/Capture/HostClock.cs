using System.Diagnostics;
using MeetingAssistant.Core.Platform;

namespace MeetingAssistant.Core.Capture;

/// The monotonic host clock all capture timestamps use, in seconds (spec 01-recording, "Track writer":
/// "a monotonic host clock captured when the session object is created"). On Windows
/// `Stopwatch.GetTimestamp()` is QueryPerformanceCounter, so a WASAPI `pu64QPCPosition`
/// (100 ns units) converts with <see cref="FromQpc100ns"/> onto the same scale.
public static class HostClock
{
    /// Seconds on the host clock.
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// A WASAPI QPC position (100 ns units) as host-clock seconds.
    public static double FromQpc100ns(long qpc100ns) => qpc100ns * 1e-7;
}

/// An <see cref="IAudioSource"/> that knows when each buffer was captured. The recorder uses the
/// time to keep tracks aligned (silence for start delays and dropouts). Implementations raise
/// both <see cref="IAudioSource.Samples"/> and <see cref="TimedSamples"/> for every buffer.
public interface ITimedAudioSource : IAudioSource
{
    /// 16 kHz mono samples and the host-clock time (<see cref="HostClock"/>) of their first sample.
    event Action<float[], double>? TimedSamples;
}

/// Level meter maths shared by the recorder screen, the floating window and the dictation HUD.
public static class LevelMath
{
    /// Recorder meters publish levels quantized to 2 dB steps; below −60 dB reads as 0
    /// (spec 01-recording, "Recording lifecycle" step 6).
    public static float Quantize(float v)
    {
        if (!(v > 1e-6f)) return 0;
        var db = MathF.Round(20 * MathF.Log10(v) / 2, MidpointRounding.AwayFromZero) * 2;
        return db < -60 ? 0 : MathF.Pow(10, db / 20);
    }

    /// Meter fill 0…1 for −60…0 dB ("Microphone"/"Call audio" meters; red above 0.9).
    public static double Meter(float rms)
    {
        var db = 20 * Math.Log10(Math.Max(rms, 1e-6));
        return Math.Clamp((db + 60) / 60, 0, 1);
    }

    /// Floating window level dot: lit above ≈ −45 dB (spec 01-recording, "Floating live transcript window").
    public const float DotThreshold = 0.0056f;

    /// Dictation HUD bars 0…1: (dB + 55) / 45 with dB of max(rms, 1e-5) (spec 04 §1.7).
    public static double DictationBars(float rms)
    {
        var db = 20 * Math.Log10(Math.Max(rms, 1e-5));
        return Math.Clamp((db + 55) / 45, 0, 1);
    }

    /// RMS of a buffer (0 for an empty one).
    public static float Rms(ReadOnlySpan<float> s)
    {
        if (s.Length == 0) return 0;
        double sum = 0;
        foreach (var x in s) sum += x * x;
        return (float)Math.Sqrt(sum / s.Length);
    }
}
