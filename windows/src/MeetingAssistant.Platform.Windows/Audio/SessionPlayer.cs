using MeetingAssistant.Core;
using MeetingAssistant.Core.Audio;
using MeetingAssistant.Core.Model;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingAssistant.Platform.Windows.Audio;

/// Plays all tracks of a session together, in sync (spec 01-recording, "Playback"): the tracks are
/// mixed sample-accurately into one stream (so they cannot drift), resampled to the output device's
/// format and played with WASAPI shared mode. Reads `<track>.caf` (Mac) or `<track>.wav`. The UI
/// polls <see cref="CurrentTime"/> every 0.2 s. Use from the UI thread.
public sealed class SessionPlayer : IDisposable
{
    readonly List<TrackReader> _tracks = new();
    WasapiOut? _out;
    MixProvider? _mix;
    double _startOffset;
    double _pausedAt;

    public SessionPlayer(string sessionId, IEnumerable<Track> tracks)
    {
        foreach (var t in tracks)
        {
            var path = AppPaths.TrackFile(sessionId, t);
            if (File.Exists(path)) _tracks.Add(TrackReader.Open(path));
        }
    }

    public bool IsPlaying { get; private set; }
    public double Duration => _tracks.Count == 0 ? 0 : _tracks.Max(t => t.Length) / (double)AudioFile.SampleRate;

    /// Raised (on the UI thread when created there) when playback reached the end.
    public event Action? Finished;

    /// Playback position in seconds.
    public double CurrentTime
    {
        get
        {
            if (!IsPlaying || _out is null) return _pausedAt;
            var f = _out.OutputWaveFormat;
            double played = _out.GetPosition() / (double)f.AverageBytesPerSecond;
            return Math.Min(Duration, _startOffset + played);
        }
    }

    public void Toggle()
    {
        if (IsPlaying) Pause(); else Play(_pausedAt);
    }

    /// Play from <paramref name="t"/> seconds (clamped to 0…duration).
    public void Play(double t)
    {
        StopOutput();
        if (_tracks.Count == 0) return;
        t = Math.Clamp(t, 0, Duration);
        long pos = (long)(t * AudioFile.SampleRate);
        foreach (var tr in _tracks) tr.Position = pos;
        _mix = new MixProvider(_tracks);
        var device = DeviceFormat();
        ISampleProvider chain = _mix;
        if (device.SampleRate != AudioFile.SampleRate) chain = new WdlResamplingSampleProvider(chain, device.SampleRate);
        chain = new MonoToChannels(chain, device.Channels);
        var o = new WasapiOut(AudioClientShareMode.Shared, true, 100);
        o.Init(chain.ToWaveProvider());
        o.PlaybackStopped += OnStopped;
        _out = o;
        _startOffset = t;
        IsPlaying = true;
        o.Play();
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (sender != _out) return;                          // an output we replaced
        _pausedAt = _mix?.Ended == true ? Duration : CurrentTimeFromMix();
        IsPlaying = false;
        DisposeOutput();
        Finished?.Invoke();
    }

    double CurrentTimeFromMix() => _mix is null ? _pausedAt : Math.Min(Duration, _mix.Position / (double)AudioFile.SampleRate);

    public void Pause()
    {
        if (!IsPlaying) return;
        _pausedAt = CurrentTime;
        StopOutput();
    }

    /// Jump to <paramref name="t"/>; keeps playing if it was playing.
    public void Seek(double t)
    {
        if (IsPlaying) Play(t);
        else _pausedAt = Math.Clamp(t, 0, Duration);
    }

    void StopOutput()
    {
        IsPlaying = false;
        var o = _out;
        if (o is null) return;
        o.PlaybackStopped -= OnStopped;
        _out = null;
        try { o.Stop(); } catch (InvalidOperationException) { }
        o.Dispose();
    }

    void DisposeOutput()
    {
        var o = _out;
        _out = null;
        if (o is null) return;
        o.PlaybackStopped -= OnStopped;
        o.Dispose();
    }

    static WaveFormat DeviceFormat()
    {
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var client = dev.AudioClient;
        var mix = client.MixFormat;
        client.Dispose();
        return WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
    }

    public void Dispose()
    {
        StopOutput();
        foreach (var t in _tracks) t.Dispose();
        _tracks.Clear();
    }

    /// One track as 16 kHz mono samples with a seekable position: a 16 kHz mono PCM16 WAV is streamed
    /// from disk, anything else (Mac CAF) is decoded into memory.
    sealed class TrackReader : IDisposable
    {
        readonly WaveFileReader? _wav;
        readonly float[]? _mem;
        readonly byte[] _buf = new byte[32_768];
        long _pos;

        TrackReader(WaveFileReader wav) { _wav = wav; Length = wav.Length / 2; }
        TrackReader(float[] mem) { _mem = mem; Length = mem.Length; }

        public long Length { get; }

        public static TrackReader Open(string path)
        {
            if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                var r = new WaveFileReader(path);
                var f = r.WaveFormat;
                if (f.Encoding == WaveFormatEncoding.Pcm && f.SampleRate == AudioFile.SampleRate && f.Channels == 1 && f.BitsPerSample == 16)
                    return new TrackReader(r);
                r.Dispose();
            }
            return new TrackReader(AudioFile.Read(path));
        }

        public long Position
        {
            get => _pos;
            set
            {
                _pos = Math.Clamp(value, 0, Length);
                if (_wav is not null) _wav.Position = _pos * 2;
            }
        }

        /// Reads up to <paramref name="count"/> samples into <paramref name="dst"/>; returns how many.
        public int Read(Span<float> dst)
        {
            int n = (int)Math.Min(dst.Length, Length - _pos);
            if (n <= 0) return 0;
            if (_mem is not null)
            {
                _mem.AsSpan((int)_pos, n).CopyTo(dst);
            }
            else
            {
                int done = 0;
                while (done < n)
                {
                    int want = Math.Min((n - done) * 2, _buf.Length);
                    int got = _wav!.Read(_buf, 0, want);
                    if (got <= 0) break;
                    for (int i = 0; i < got / 2; i++) dst[done + i] = BitConverter.ToInt16(_buf, i * 2) / 32768f;
                    done += got / 2;
                }
                n = done;
            }
            _pos += n;
            return n;
        }

        public void Dispose() => _wav?.Dispose();
    }

    /// Sums the tracks (clamped to −1…1); ends when every track ended.
    sealed class MixProvider(List<TrackReader> tracks) : ISampleProvider
    {
        float[] _tmp = new float[4096];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioFile.SampleRate, 1);
        public long Position { get; private set; }
        public bool Ended { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_tmp.Length < count) _tmp = new float[count];
            var dst = buffer.AsSpan(offset, count);
            dst.Clear();
            int max = 0;
            foreach (var t in tracks)
            {
                int n = t.Read(_tmp.AsSpan(0, count));
                for (int i = 0; i < n; i++) dst[i] += _tmp[i];
                max = Math.Max(max, n);
            }
            for (int i = 0; i < max; i++) dst[i] = Math.Clamp(dst[i], -1f, 1f);
            Position += max;
            if (max == 0) Ended = true;
            return max;
        }
    }

    /// Copies a mono stream to every output channel.
    sealed class MonoToChannels(ISampleProvider source, int channels) : ISampleProvider
    {
        float[] _tmp = new float[4096];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, Math.Max(1, channels));

        public int Read(float[] buffer, int offset, int count)
        {
            int ch = WaveFormat.Channels;
            int frames = count / ch;
            if (_tmp.Length < frames) _tmp = new float[frames];
            int n = source.Read(_tmp, 0, frames);
            for (int f = 0; f < n; f++)
                for (int c = 0; c < ch; c++) buffer[offset + f * ch + c] = _tmp[f];
            return n * ch;
        }
    }
}
