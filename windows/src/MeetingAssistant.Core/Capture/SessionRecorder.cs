using System.Globalization;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Platform;

namespace MeetingAssistant.Core.Capture;

/// Records one meeting: the microphone and (Call/Hybrid) the call audio into two time-aligned
/// 16 kHz mono tracks (`mic.wav`, `system.wav`) plus `session.json` (spec 01-recording,
/// "Recording lifecycle" and "Track writer"). The audio comes from <see cref="IAudioSource"/>s:
/// WASAPI on Windows, files or fakes in tests and msctl.
///
/// Lifecycle: constructor (creates the session folder, status `recording`) → <see cref="Start"/>
/// (writes session.json, starts mic then call audio) → <see cref="Stop"/> (pads both tracks to the
/// longer one, writes `durationSec` and status `recorded`).
public sealed class SessionRecorder : IDisposable
{
    readonly Func<double> _clock;
    readonly double _start;
    readonly object _lock = new();
    IAudioSource? _mic, _system;
    TrackWriter? _micWriter, _systemWriter;
    readonly List<Action> _unsubscribe = new();
    bool _started, _stopped;
    Action<float[]>? _onMic, _onSystem;

    /// The session as written at start (status `recording`, duration 0).
    public SessionInfo Info { get; }

    /// Live audio for the live transcriber (16 kHz mono, including inserted silence). May be set
    /// before or after <see cref="Start"/>.
    public Action<float[]>? OnMicSamples
    {
        get => _onMic;
        set { _onMic = value; if (_micWriter is not null) _micWriter.OnSamples = value; }
    }

    /// See <see cref="OnMicSamples"/>; the call-audio track.
    public Action<float[]>? OnSystemSamples
    {
        get => _onSystem;
        set { _onSystem = value; if (_systemWriter is not null) _systemWriter.OnSamples = value; }
    }

    /// A source reported a failure while recording (track, message). The recording continues;
    /// the track is filled with silence until the source delivers again.
    public event Action<Track, string>? SourceFailed;

    /// Creates the session folder (`yyyy-MM-dd_HH-mm-ss`, `_2`, `_3`… when taken) and the info.
    /// <param name="title">Trimmed title, or null/empty for "Meeting &lt;date&gt; at &lt;time&gt;".</param>
    /// <param name="language">"german"/"english", or null for automatic (omitted from JSON).</param>
    /// <param name="clock">Host clock in seconds (default <see cref="HostClock.Now"/>); tests inject one.</param>
    /// <param name="startedAt">Wall-clock start (default now); tests inject one.</param>
    public SessionRecorder(MeetingMode mode, string? title = null, int? expectedSpeakers = null, string? language = null,
                           Func<double>? clock = null, DateTime? startedAt = null)
    {
        _clock = clock ?? (() => HostClock.Now);
        var start = startedAt ?? DateTime.Now;
        AppPaths.Ensure();
        var id = CreateSessionFolder(FolderName(start));
        Info = new SessionInfo
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle(start, CultureInfo.CurrentCulture) : title.Trim(),
            StartedAt = start.ToUniversalTime(),
            DurationSec = 0,
            Mode = mode,
            Tracks = mode.RecordsSystemAudio() ? new List<Track> { Track.Mic, Track.System } : new List<Track> { Track.Mic },
            ExpectedSpeakers = expectedSpeakers,
            Language = language == "auto" ? null : language,
            Status = SessionStatus.Recording,
        };
        _start = _clock();
    }

    /// "yyyy-MM-dd_HH-mm-ss" of a local time: the base of a session folder name.
    public static string FolderName(DateTime local) => local.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

    /// "Meeting 10 Nov 2026 at 14:03" (medium date, short time in the given culture).
    public static string DefaultTitle(DateTime local, CultureInfo culture) =>
        $"Meeting {local.ToString("d MMM yyyy", culture)} at {local.ToString(culture.DateTimeFormat.ShortTimePattern, culture)}";

    /// Creates `Sessions/<base>` or `<base>_2`, `<base>_3`… and returns the id. Creating the folder
    /// is the reservation, so two sessions started in the same second never share a folder.
    public static string CreateSessionFolder(string baseName)
    {
        AppPaths.Ensure();
        var id = baseName;
        for (int n = 2; ; n++)
        {
            var dir = AppPaths.Session(id);
            if (!Directory.Exists(dir) && !File.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                return id;
            }
            id = $"{baseName}_{n}";
        }
    }

    /// Writes session.json and starts the sources: mic first, then call audio (required for Call
    /// and Hybrid, ignored for In person). If the call audio fails to start, the mic is stopped and
    /// the error is rethrown ("Could not start recording: …" in the UI).
    public void Start(IAudioSource mic, IAudioSource? system = null)
    {
        lock (_lock)
        {
            if (_started) throw new InvalidOperationException("The recorder was already started.");
            _started = true;
        }
        if (Info.Mode.RecordsSystemAudio() && system is null)
            throw new ArgumentException("This meeting mode records the call audio; a system source is needed.", nameof(system));
        JsonIO.Write(Info, AppPaths.Info(Info.Id));

        _micWriter = new TrackWriter(AppPaths.NewTrack(Info.Id, Track.Mic), _start, _clock) { OnSamples = _onMic };
        _mic = mic;
        Attach(mic, _micWriter, Track.Mic);
        try
        {
            mic.Start();
            if (Info.Mode.RecordsSystemAudio())
            {
                _systemWriter = new TrackWriter(AppPaths.NewTrack(Info.Id, Track.System), _start, _clock) { OnSamples = _onSystem };
                _system = system;
                Attach(system!, _systemWriter, Track.System);
                system!.Start();
            }
        }
        catch
        {
            mic.Stop();
            system?.Stop();
            Detach();
            _micWriter?.Dispose();
            _systemWriter?.Dispose();
            throw;
        }
    }

    void Attach(IAudioSource source, TrackWriter writer, Track track)
    {
        if (source is ITimedAudioSource timed)
        {
            Action<float[], double> h = (s, t) => writer.Append(s, t);
            timed.TimedSamples += h;
            _unsubscribe.Add(() => timed.TimedSamples -= h);
        }
        else
        {
            // Without timestamps, the buffer is taken to end now.
            Action<float[]> h = s => writer.Append(s, _clock() - s.Length / 16_000.0);
            source.Samples += h;
            _unsubscribe.Add(() => source.Samples -= h);
        }
        Action<string> f = msg => SourceFailed?.Invoke(track, msg);
        source.Failed += f;
        _unsubscribe.Add(() => source.Failed -= f);
    }

    void Detach()
    {
        foreach (var u in _unsubscribe) u();
        _unsubscribe.Clear();
    }

    /// Seconds since the session was created (host clock).
    public double Elapsed => _clock() - _start;
    public float MicLevel => _micWriter?.Level ?? 0;
    public float SystemLevel => _systemWriter?.Level ?? 0;

    /// Stops capture, pads both tracks to the longer duration, closes the files and writes
    /// session.json with `durationSec` and status `recorded`. Returns the final info.
    public SessionInfo Stop()
    {
        lock (_lock)
        {
            if (_stopped) throw new InvalidOperationException("The recorder was already stopped.");
            _stopped = true;
        }
        _mic?.Stop();
        _system?.Stop();
        Detach();
        double duration = Math.Max(_micWriter?.DurationSeconds ?? 0, _systemWriter?.DurationSeconds ?? 0);
        _micWriter?.Finish(duration);
        _systemWriter?.Finish(duration);
        var final = Info.Clone();
        final.DurationSec = duration;
        final.Status = SessionStatus.Recorded;
        JsonIO.Write(final, AppPaths.Info(Info.Id));
        return final;
    }

    /// Releases the files without writing the final state (use <see cref="Stop"/> for a normal end).
    public void Dispose()
    {
        Detach();
        _micWriter?.Dispose();
        _systemWriter?.Dispose();
    }
}
