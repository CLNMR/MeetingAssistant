using MeetingAssistant.Core.Audio;

namespace MeetingAssistant.Core.Capture;

/// Writes one 16 kHz mono track, aligned to the session clock (spec 01-recording, "Track writer
/// (time alignment and files)"): silence is inserted for the delay before the first buffer and for
/// any dropout (device switch, rebuilt loopback), so word times on both tracks share one clock.
/// With a null path the writer is listen-only: samples only go to <see cref="OnSamples"/>.
/// Thread-safe: capture threads call <see cref="Append"/>; the UI reads <see cref="Level"/>.
public sealed class TrackWriter : IDisposable
{
    /// Gaps up to this many samples (100 ms) are jitter and ignored.
    public const int GapThresholdSamples = 1600;
    /// The file header is rewritten after this many new samples (1 s), so a crash loses at most ~1 s.
    public const int FlushEverySamples = AudioFile.SampleRate;

    readonly WavWriter? _wav;
    readonly double _sessionStart;
    readonly Func<double> _now;
    readonly object _lock = new();
    long _written;
    long _sinceFlush;
    float _level;
    double _levelTime = double.NegativeInfinity;
    bool _closed;

    public string? Path { get; }

    /// Called (under the writer's lock, on the capture thread) with every block written,
    /// including inserted silence, e.g. to feed the live transcriber.
    public Action<float[]>? OnSamples { get; set; }

    /// <param name="path">WAV file to create, or null for listen-only.</param>
    /// <param name="sessionStart">Session start on the host clock (seconds).</param>
    /// <param name="now">Host clock (default <see cref="HostClock.Now"/>); used for the level meter's age.</param>
    public TrackWriter(string? path, double sessionStart, Func<double>? now = null)
    {
        Path = path;
        _sessionStart = sessionStart;
        _now = now ?? (() => HostClock.Now);
        if (path is not null) _wav = WavWriter.Create(path);
    }

    public long SamplesWritten { get { lock (_lock) return _written; } }
    public double DurationSeconds => SamplesWritten / (double)AudioFile.SampleRate;

    /// Smoothed RMS (0…1): peak-hold `max(rms, previous × 0.8)` per buffer; 0 when no buffer
    /// arrived within 0.3 s (the call audio is silent while nothing plays).
    public float Level
    {
        get
        {
            lock (_lock) return _now() - _levelTime > 0.3 ? 0 : _level;
        }
    }

    /// Append 16 kHz mono samples. <paramref name="captureTime"/> is the host-clock time of the first
    /// sample (null when unknown: no alignment for this buffer).
    public void Append(ReadOnlySpan<float> samples, double? captureTime)
    {
        if (samples.Length == 0) return;
        lock (_lock)
        {
            if (_closed) return;
            if (captureTime is double t && t > _sessionStart)
            {
                long expected = (long)((t - _sessionStart) * AudioFile.SampleRate);
                long gap = expected - _written;
                // Pad only for real gaps (> 100 ms); never drop samples when ahead.
                if (gap > GapThresholdSamples) WriteSilenceLocked(gap);
            }
            WriteLocked(samples.ToArray());
            var rms = LevelMath.Rms(samples);
            _level = Math.Max(rms, _level * 0.8f);
            _levelTime = _now();
        }
    }

    void WriteSilenceLocked(long count)
    {
        while (count > 0)
        {
            var n = (int)Math.Min(count, AudioFile.SampleRate);
            WriteLocked(new float[n]);
            count -= n;
        }
    }

    void WriteLocked(float[] block)
    {
        if (_wav is not null)
        {
            _wav.Append(block);
            _sinceFlush += block.Length;
            if (_sinceFlush >= FlushEverySamples) { _wav.Flush(); _sinceFlush = 0; }
        }
        _written += block.Length;
        OnSamples?.Invoke(block);
    }

    /// Make everything written so far readable after a crash.
    public void Flush()
    {
        lock (_lock) { if (!_closed) { _wav?.Flush(); _sinceFlush = 0; } }
    }

    /// Pad with silence to <paramref name="seconds"/> (so both tracks end together) and close the file.
    public void Finish(double seconds)
    {
        lock (_lock)
        {
            if (_closed) return;
            long target = (long)(seconds * AudioFile.SampleRate);
            if (target > _written) WriteSilenceLocked(target - _written);
            _closed = true;
            _wav?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            _wav?.Dispose();
        }
    }
}
