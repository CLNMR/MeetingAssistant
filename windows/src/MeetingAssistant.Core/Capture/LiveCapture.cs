using MeetingAssistant.Core.Platform;

namespace MeetingAssistant.Core.Capture;

/// Captures the mic and (optionally) the call audio without saving anything, for the floating live
/// transcript (spec 01-recording, "Floating live transcript window"). Audio goes to the current
/// segment: a pair of callbacks with their own time origin, so the live transcriber can be replaced
/// (segment rotation) without restarting capture. Each segment uses listen-only track writers, so
/// dropouts are filled with silence exactly like a recording.
public sealed class LiveCapture : IDisposable
{
    readonly Func<double> _clock;
    readonly object _lock = new();
    IAudioSource? _mic, _system;
    TrackWriter? _micWriter, _systemWriter;
    readonly List<Action> _unsubscribe = new();

    public bool IsRunning { get; private set; }
    public bool CapturesSystem { get; private set; }

    /// A source failed (track, message); capture continues.
    public event Action<Model.Track, string>? SourceFailed;

    public LiveCapture(Func<double>? clock = null) => _clock = clock ?? (() => HostClock.Now);

    /// Start the sources (call audio only when <paramref name="system"/> is given). If the call audio
    /// cannot start, the mic is stopped again and the error rethrown.
    public void Start(IAudioSource mic, IAudioSource? system)
    {
        if (IsRunning) return;
        _mic = mic; _system = system;
        CapturesSystem = system is not null;
        Hook(mic, () => _micWriter, Model.Track.Mic);
        if (system is not null) Hook(system, () => _systemWriter, Model.Track.System);
        try
        {
            mic.Start();
            system?.Start();
        }
        catch
        {
            mic.Stop();
            foreach (var u in _unsubscribe) u();
            _unsubscribe.Clear();
            throw;
        }
        IsRunning = true;
    }

    void Hook(IAudioSource source, Func<TrackWriter?> writer, Model.Track track)
    {
        TrackWriter? Current() { lock (_lock) return writer(); }
        if (source is ITimedAudioSource timed)
        {
            Action<float[], double> h = (s, t) => Current()?.Append(s, t);
            timed.TimedSamples += h;
            _unsubscribe.Add(() => timed.TimedSamples -= h);
        }
        else
        {
            Action<float[]> h = s => Current()?.Append(s, _clock() - s.Length / 16_000.0);
            source.Samples += h;
            _unsubscribe.Add(() => source.Samples -= h);
        }
        Action<string> f = m => SourceFailed?.Invoke(track, m);
        source.Failed += f;
        _unsubscribe.Add(() => source.Failed -= f);
    }

    /// Route all audio from now on to new callbacks; times restart at 0.
    public void BeginSegment(Action<float[]> onMic, Action<float[]>? onSystem)
    {
        var now = _clock();
        var mw = new TrackWriter(null, now, _clock) { OnSamples = onMic };
        TrackWriter? sw = CapturesSystem && onSystem is not null ? new TrackWriter(null, now, _clock) { OnSamples = onSystem } : null;
        lock (_lock) { _micWriter = mw; _systemWriter = sw; }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _mic?.Stop();
        _system?.Stop();
        foreach (var u in _unsubscribe) u();
        _unsubscribe.Clear();
        lock (_lock) { _micWriter = null; _systemWriter = null; }
        IsRunning = false;
    }

    public float MicLevel { get { lock (_lock) return _micWriter?.Level ?? 0; } }
    public float SystemLevel { get { lock (_lock) return _systemWriter?.Level ?? 0; } }

    public void Dispose() => Stop();
}
