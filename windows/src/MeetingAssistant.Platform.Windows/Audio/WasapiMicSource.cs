using System.Runtime.InteropServices;
using MeetingAssistant.Core.Capture;
using NAudio.CoreAudioApi;

namespace MeetingAssistant.Platform.Windows.Audio;

/// The microphone has been blocked in Settings › Privacy & security › Microphone.
public sealed class MicrophoneAccessDeniedException(Exception inner)
    : Exception("MeetingAssistant has no access to the microphone. Allow it in Settings › Privacy & security › Microphone, then try again.", inner);

/// Microphone capture with WASAPI shared mode on the chosen endpoint (spec 01-recording,
/// "Microphone"): native format, downmixed and resampled to 16 kHz mono, QPC-timestamped.
/// Self-repair: endpoint notifications and device invalidation are debounced (0.4 s); then, unless
/// the target device (chosen-or-default) and its rate (±1 Hz) and channel count are unchanged, the
/// stream is rebuilt on the target, up to 10 attempts 0.5 s apart. Only the resolved device is ever
/// opened (never the default when another one was chosen).
public sealed class WasapiMicSource : ITimedAudioSource
{
    public const double DebounceSeconds = 0.4;
    public const int RestartAttempts = 10;
    public const double RestartDelaySeconds = 0.5;

    readonly string? _preferred;
    readonly object _lock = new();
    WasapiStream? _stream;
    DeviceNotifications? _notifications;
    Timer? _debounce;
    bool _running;
    int _rate, _channels;

    public event Action<float[]>? Samples;
    public event Action<float[], double>? TimedSamples;
    public event Action<string>? Failed;

    /// Endpoint id of the device in use (null while stopped).
    public string? CurrentDeviceId { get; private set; }

    /// <param name="preferredDeviceId">Endpoint id from the preference `inputDeviceUID`; null = system default.</param>
    public WasapiMicSource(string? preferredDeviceId) => _preferred = string.IsNullOrEmpty(preferredDeviceId) ? null : preferredDeviceId;

    /// Opens the device. Throws <see cref="MicrophoneAccessDeniedException"/> when the privacy setting
    /// blocks desktop apps, InvalidOperationException when there is no input device.
    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            OpenLocked();
            _running = true;
            _debounce = new Timer(_ => RestartIfNeeded(), null, Timeout.Infinite, Timeout.Infinite);
            _notifications = new DeviceNotifications((_, _) => ScheduleRestart());
        }
    }

    void OpenLocked()
    {
        var id = AudioDevices.Resolve(_preferred) ?? throw new InvalidOperationException("Microphone unavailable: no input device");
        var stream = new WasapiStream("WasapiMic", () => Open(id), Emit, OnStreamError);
        try
        {
            stream.Start();
        }
        catch (COMException e) when (e.HResult == WasapiStream.E_ACCESSDENIED)
        {
            throw new MicrophoneAccessDeniedException(e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new MicrophoneAccessDeniedException(e);
        }
        _stream = stream;
        CurrentDeviceId = id;
        _rate = stream.Format!.SampleRate;
        _channels = stream.Format.Channels;
    }

    static OpenedClient Open(string id)
    {
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDevice(id);
        var client = dev.AudioClient;
        var fmt = client.MixFormat;
        // 100 ms buffer, event driven, the engine's shared format (converted by us).
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, 1_000_000, 0, fmt, Guid.Empty);
        return new OpenedClient(client, fmt, EventDriven: true);
    }

    void Emit(float[] s, double t)
    {
        Samples?.Invoke(s);
        TimedSamples?.Invoke(s, t);
    }

    void OnStreamError(Exception e) => ScheduleRestart();

    void ScheduleRestart()
    {
        // Change events come in bursts: react once they settle.
        try { _debounce?.Change(TimeSpan.FromSeconds(DebounceSeconds), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    void RestartIfNeeded()
    {
        lock (_lock)
        {
            if (!_running) return;
            var target = AudioDevices.Resolve(_preferred);
            if (_stream is { IsAlive: true } && target is not null && target == CurrentDeviceId && FormatUnchanged(target)) return;
            _stream?.Stop();
            _stream = null;
            string? last = null;
            for (int attempt = 0; attempt < RestartAttempts && _running; attempt++)
            {
                try { OpenLocked(); return; }
                catch (Exception e)
                {
                    last = e.Message;
                    Thread.Sleep(TimeSpan.FromSeconds(RestartDelaySeconds));   // a new device may need a moment
                }
            }
            if (_running) Failed?.Invoke($"Microphone unavailable: {last}");
        }
    }

    bool FormatUnchanged(string id)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDevice(id);
            var client = dev.AudioClient;
            var f = client.MixFormat;
            client.Dispose();
            return Math.Abs(f.SampleRate - _rate) < 1 && f.Channels == _channels;
        }
        catch (COMException) { return false; }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            _notifications?.Dispose(); _notifications = null;
            _debounce?.Dispose(); _debounce = null;
            _stream?.Stop(); _stream = null;
            CurrentDeviceId = null;
        }
    }

    public void Dispose() => Stop();
}
