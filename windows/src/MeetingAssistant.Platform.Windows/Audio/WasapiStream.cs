using System.Runtime.InteropServices;
using MeetingAssistant.Core.Capture;
using MeetingAssistant.Platform.Windows.Interop;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingAssistant.Platform.Windows.Audio;

/// What an opener hands to <see cref="WasapiStream"/>: an initialized shared-mode client, the format
/// it delivers and whether it was initialized with AUDCLNT_STREAMFLAGS_EVENTCALLBACK.
internal sealed record OpenedClient(AudioClient Client, WaveFormat Format, bool EventDriven);

/// One WASAPI capture stream on its own MTA thread: reads packets with their QPC timestamps
/// (`IAudioCaptureClient::GetBuffer` → `pu64QPCPosition`, spec 01-recording "Track writer" Windows
/// notes), downmixes to mono, resamples to 16 kHz and calls <c>onSamples(samples, hostClockTime)</c>.
/// Any COM error (e.g. AUDCLNT_E_DEVICE_INVALIDATED) ends the stream and is reported to
/// <c>onError</c>; the owner decides whether to rebuild.
internal sealed class WasapiStream : IDisposable
{
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);

    readonly Func<OpenedClient> _open;
    readonly Action<float[], double> _onSamples;
    readonly Action<Exception> _onError;
    readonly string _name;
    Thread? _thread;
    readonly ManualResetEventSlim _stop = new();
    public WaveFormat? Format { get; private set; }
    /// False once the stream ended (stopped or failed).
    public bool IsAlive { get; private set; }

    public WasapiStream(string name, Func<OpenedClient> open, Action<float[], double> onSamples, Action<Exception> onError)
    {
        _name = name;
        _open = open;
        _onSamples = onSamples;
        _onError = onError;
    }

    /// Opens the client on the capture thread and starts it; throws what the opener threw.
    public void Start(TimeSpan? timeout = null)
    {
        Exception? openError = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready, e => openError = e)) { IsBackground = true, Name = _name, Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!ready.Wait(timeout ?? TimeSpan.FromSeconds(10)))
        {
            _stop.Set();
            throw new TimeoutException($"{_name}: the audio device did not start in time.");
        }
        if (openError is not null)
        {
            _thread.Join();
            throw openError;
        }
    }

    void Run(ManualResetEventSlim ready, Action<Exception> setOpenError)
    {
        Native.CoInitializeEx(0, Native.COINIT_MULTITHREADED);
        OpenedClient opened;
        AudioCaptureClient capture;
        using var evt = new AutoResetEvent(false);
        try
        {
            opened = _open();
            if (opened.EventDriven) opened.Client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle());
            capture = opened.Client.AudioCaptureClient;
            Format = opened.Format;
            opened.Client.Start();
            IsAlive = true;
        }
        catch (Exception e)
        {
            setOpenError(e);
            ready.Set();
            return;
        }
        ready.Set();

        var fmt = opened.Format;
        int channels = Math.Max(1, fmt.Channels);
        var resampler = new StreamingResampler(fmt.SampleRate);
        var conv = SampleConverter.For(fmt);
        try
        {
            while (!_stop.IsSet)
            {
                if (opened.EventDriven) evt.WaitOne(100); else Thread.Sleep(10);
                if (_stop.IsSet) break;
                int packet = capture.GetNextPacketSize();
                while (packet > 0 && !_stop.IsSet)
                {
                    var data = capture.GetBuffer(out int frames, out var flags, out long _, out long qpc);
                    float[] interleaved = new float[frames * channels];
                    if ((flags & AudioClientBufferFlags.Silent) == 0 && frames > 0)
                        conv(data, interleaved, frames * channels);
                    capture.ReleaseBuffer(frames);
                    double time = qpc > 0 && (flags & AudioClientBufferFlags.TimestampError) == 0
                        ? HostClock.FromQpc100ns(qpc)
                        : HostClock.Now - frames / (double)fmt.SampleRate;
                    var mono = StreamingResampler.Downmix(interleaved, channels);
                    var out16 = resampler.Process(mono);
                    if (out16.Length > 0) _onSamples(out16, time);
                    packet = capture.GetNextPacketSize();
                }
            }
        }
        catch (Exception e)
        {
            IsAlive = false;
            Cleanup(opened.Client, capture);
            _onError(e);
            return;
        }
        IsAlive = false;
        Cleanup(opened.Client, capture);
    }

    static void Cleanup(AudioClient client, AudioCaptureClient capture)
    {
        try { client.Stop(); } catch (COMException) { }
        try { capture.Dispose(); } catch (COMException) { }
        try { client.Dispose(); } catch (COMException) { }
    }

    public void Stop()
    {
        _stop.Set();
        var t = _thread;
        if (t is not null && t != Thread.CurrentThread) t.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    public void Dispose() => Stop();
}

/// Converts raw WASAPI buffers (float32, PCM 16/24/32) into float samples.
internal static unsafe class SampleConverter
{
    public delegate void Converter(nint src, float[] dst, int count);
    static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static Converter For(WaveFormat f)
    {
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
                       || (f is WaveFormatExtensible x && x.SubFormat == IeeeFloat);
        int bits = f.BitsPerSample;
        if (isFloat && bits == 32) return (s, d, n) => new ReadOnlySpan<float>((void*)s, n).CopyTo(d);
        if (bits == 16) return (s, d, n) => { var p = (short*)s; for (int i = 0; i < n; i++) d[i] = p[i] / 32768f; };
        if (bits == 24) return (s, d, n) =>
        {
            var p = (byte*)s;
            for (int i = 0; i < n; i++)
            {
                int v = p[3 * i] | (p[3 * i + 1] << 8) | (p[3 * i + 2] << 16);
                if ((v & 0x800000) != 0) v -= 0x1000000;
                d[i] = v / 8388608f;
            }
        };
        if (bits == 32) return (s, d, n) => { var p = (int*)s; for (int i = 0; i < n; i++) d[i] = p[i] / 2147483648f; };
        throw new NotSupportedException($"Unsupported capture format: {f}");
    }
}
