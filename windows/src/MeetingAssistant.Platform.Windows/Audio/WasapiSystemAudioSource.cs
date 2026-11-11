using System.Diagnostics;
using System.Runtime.InteropServices;
using MeetingAssistant.Core.Capture;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingAssistant.Platform.Windows.Audio;

/// How the call audio is captured.
public enum LoopbackKind { None, ProcessExcludingSelf, Endpoint }

/// Call audio ("system audio", spec 01-recording "System audio"): everything played on the computer
/// except this app's own playback, as one 16 kHz mono, QPC-timestamped track.
/// 1. WASAPI process loopback excluding this process tree (Windows 10 2004+).
/// 2. Fallback: classic loopback of the default render endpoint (then the app's own playback is
///    included; the app plays nothing while recording).
/// Both are rebuilt when the default output changes.
/// Any stream error (device invalidated, audio service restart) rebuilds the capture. WASAPI
/// loopback delivers no packets while nothing plays; that is normal (the track writer pads), so
/// there is no "no buffers" watchdog here.
public sealed class WasapiSystemAudioSource : ITimedAudioSource
{
    readonly object _lock = new();
    readonly bool _allowProcessLoopback;
    WasapiStream? _stream;
    DeviceNotifications? _notifications;
    bool _running;
    int _rebuilding;

    public event Action<float[]>? Samples;
    public event Action<float[], double>? TimedSamples;
    public event Action<string>? Failed;

    /// The capture method in use.
    public LoopbackKind Kind { get; private set; }

    /// <param name="allowProcessLoopback">False forces the endpoint loopback (diagnostics).</param>
    public WasapiSystemAudioSource(bool allowProcessLoopback = true) => _allowProcessLoopback = allowProcessLoopback;

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            OpenLocked();
            _running = true;
            _notifications = new DeviceNotifications((what, _) =>
            {
                // Rebuild on the new default output (spec: "Default output device changed → rebuild
                // immediately"). Process loopback may follow the default by itself; rebuilding is cheap
                // and the short gap is padded by the track writer.
                if (what == "defaultRender") RebuildAsync("default output changed");
            });
        }
    }

    void OpenLocked()
    {
        Exception? processError = null;
        if (_allowProcessLoopback && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            foreach (var (format, flags) in ProcessFormats())
            {
                var s = new WasapiStream("WasapiProcessLoopback", () => OpenProcessLoopback(format, flags), Emit, OnStreamError);
                try
                {
                    s.Start();
                    _stream = s;
                    Kind = LoopbackKind.ProcessExcludingSelf;
                    return;
                }
                catch (Exception e) { processError = e; }
            }
        }
        var endpoint = new WasapiStream("WasapiLoopback", OpenEndpointLoopback, Emit, OnStreamError);
        try
        {
            endpoint.Start();
        }
        catch (Exception e) when (processError is not null)
        {
            throw new InvalidOperationException($"Could not capture the call audio: {e.Message} (process loopback: {processError.Message})", e);
        }
        _stream = endpoint;
        Kind = LoopbackKind.Endpoint;
    }

    /// Formats tried for process loopback (it has no mix format): 48 kHz stereo PCM16 with automatic
    /// conversion, then exactly what Microsoft's ApplicationLoopback sample uses (44.1 kHz stereo PCM16).
    static IEnumerable<(WaveFormat, AudioClientStreamFlags)> ProcessFormats()
    {
        const AudioClientStreamFlags baseFlags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback;
        yield return (new WaveFormat(48000, 16, 2), baseFlags | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality);
        yield return (new WaveFormat(44100, 16, 2), baseFlags);
    }

    static OpenedClient OpenProcessLoopback(WaveFormat format, AudioClientStreamFlags flags)
    {
        var client = ProcessLoopback.Activate(Environment.ProcessId, include: false, TimeSpan.FromSeconds(5));
        try
        {
            client.Initialize(AudioClientShareMode.Shared, flags, 1_000_000, 0, format, Guid.Empty);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new OpenedClient(client, format, EventDriven: true);
    }

    static OpenedClient OpenEndpointLoopback()
    {
        using var en = new MMDeviceEnumerator();
        if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console)) throw new InvalidOperationException("No default output device found");
        using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        var client = dev.AudioClient;
        var fmt = client.MixFormat;
        // Polled (no event callback): event-driven loopback is not signalled on all Windows versions.
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, 1_000_000, 0, fmt, Guid.Empty);
        return new OpenedClient(client, fmt, EventDriven: false);
    }

    void Emit(float[] s, double t)
    {
        Samples?.Invoke(s);
        TimedSamples?.Invoke(s, t);
    }

    void OnStreamError(Exception e) =>
        RebuildAsync(e is COMException c && c.HResult == WasapiStream.AUDCLNT_E_DEVICE_INVALIDATED ? "device invalidated" : e.Message);

    void RebuildAsync(string reason)
    {
        if (Interlocked.Exchange(ref _rebuilding, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                lock (_lock)
                {
                    if (!_running) return;
                    Debug.WriteLine($"WasapiSystemAudioSource: rebuilding ({reason})");
                    _stream?.Stop();
                    _stream = null;
                    try { OpenLocked(); }
                    catch (Exception e)
                    {
                        // The mic keeps recording; the call track is padded with silence.
                        Failed?.Invoke($"Call audio unavailable: {e.Message}");
                    }
                }
            }
            finally { Interlocked.Exchange(ref _rebuilding, 0); }
        });
    }

    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            _notifications?.Dispose(); _notifications = null;
            _stream?.Stop(); _stream = null;
            Kind = LoopbackKind.None;
        }
    }

    public void Dispose() => Stop();
}
