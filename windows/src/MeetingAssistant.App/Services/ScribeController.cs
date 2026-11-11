using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MeetingAssistant.Core;
using MeetingAssistant.Core.Capture;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;
using MeetingAssistant.Core.Platform;
using MeetingAssistant.Core.Scribe;
using MeetingAssistant.Core.Storage;
using MeetingAssistant.Platform.Windows.Audio;
using MeetingAssistant.Platform.Windows.SystemServices;
using MeetingAssistant.Speech;

namespace MeetingAssistant.App.Services;

/// Progress of a job (transcription or model download): fraction 0…1 and a message.
public sealed record JobProgress(double Fraction, string Message);

/// <summary>
/// Scribe's state and actions, the Windows counterpart of the Mac's `AppState` (Scribe part):
/// sessions, recording (spec 01 "Recording lifecycle"), live transcript while recording, the
/// transcription queue's statuses and progress, model download, import, delete, trim, split and
/// "Transcribe Again". Lives on the UI thread; background events are marshalled to it.
/// Assist starts its recordings through <see cref="StartRecordingAsync(MeetingMode, string, Action{IReadOnlyList{LiveLine}}, Action{string})"/>.
/// </summary>
public sealed partial class ScribeController : ObservableObject
{
    readonly AppServices _services;
    readonly Dictionary<string, JobProgress> _progress = new();
    HashSet<string> _assistIds = new();
    SessionRecorder? _recorder;
    LiveTranscriber? _live;
    TranscriptSegment? _liveSegment;
    DispatcherTimer? _meterTimer;
    bool _forceLive;
    Action<IReadOnlyList<LiveLine>>? _liveLinesHook;
    Action<string>? _onStopped;

    /// The app's controller (created by App.ConfigureServices).
    public static ScribeController Shared { get; private set; } = null!;

    public ScribeController(AppServices services)
    {
        _services = services;
        Shared = this;
        _recordingMode = services.Preferences.DefaultMode;
        _modelsReady = ModelCatalog.CoreModelsReady();
        Listen = new ListenController(this, services);
        var q = services.Queue;
        q.StatusChanged += (id, status) => AppServices.Ui(() => OnStatusChanged(id, status));
        q.Progress += (id, f, m) => AppServices.Ui(() => SetProgress(id, new JobProgress(f, m)));
        q.Finished += (id, _) => AppServices.Ui(OnTranscriptionFinished);
        q.Failed += (id, e) => AppServices.Ui(() => ReportError($"Transcription failed: {e.Message}"));
        Reload();
    }

    /// Always-on listening for the floating window.
    public ListenController Listen { get; }

    // ---- Errors and navigation ----

    /// A message for the global error alert (spec 03 §1); the shell shows it.
    public event Action<string>? Error;
    /// Show a session (id) or the recorder (null) in Scribe.
    public event Action<string?>? SelectRequested;

    /// "Assist Recap" was clicked in a session's header; the Assist area opens its review.
    public static event Action<string>? AssistReviewRequested;

    /// Raises <see cref="AssistReviewRequested"/>.
    public static void RequestAssistReview(string sessionId) => AssistReviewRequested?.Invoke(sessionId);

    /// Raises <see cref="Error"/>.
    public void ReportError(string message) => Error?.Invoke(message);

    // ---- Sessions ----

    /// All sessions, newest first.
    [ObservableProperty] IReadOnlyList<SessionInfo> _sessions = Array.Empty<SessionInfo>();

    /// Re-reads the sessions from disk.
    public void Reload()
    {
        var list = SessionStore.List();
        _assistIds = list.Where(s => SessionStore.HasAssistRecord(s.Id)).Select(s => s.Id).ToHashSet();
        Sessions = list;
        TranscribingCount = list.Count(s => s.Status is SessionStatus.Queued or SessionStatus.Transcribing);
    }

    /// The session was recorded or imported with Assist (has `assist.json`).
    public bool HasAssistRecord(string id) => _assistIds.Contains(id);

    /// A session's current info from disk (null when gone).
    public SessionInfo? Session(string id) => SessionStore.TryLoad(id);

    /// Recordings being transcribed or waiting (tray "Transcribing N recording(s)…").
    [ObservableProperty] int _transcribingCount;

    // ---- Transcription queue ----

    /// Progress of a session being transcribed, or null.
    public JobProgress? ProgressOf(string id) => _progress.GetValueOrDefault(id);

    /// Raised with the session id when its progress changed or ended.
    public event Action<string>? ProgressChanged;

    void SetProgress(string id, JobProgress? p)
    {
        if (p is null) { if (!_progress.Remove(id)) return; }
        else _progress[id] = p;
        ProgressChanged?.Invoke(id);
    }

    void OnStatusChanged(string id, SessionStatus status)
    {
        if (status != SessionStatus.Transcribing) SetProgress(id, null);
        else if (!_progress.ContainsKey(id)) SetProgress(id, new JobProgress(0, "Starting"));
        Reload();
    }

    void OnTranscriptionFinished()
    {
        MarkModelsReady();
        // Mac: "Glass" sound and an attention request when the app is in the background.
        System.Media.SystemSounds.Asterisk.Play();
        if (Application.Current?.MainWindow is { } w && !w.IsActive) Infrastructure.Native.Flash(w);
    }

    /// Queues a session for transcription (ignored when already queued or running).
    public void Enqueue(string id) => _services.Queue.Enqueue(id);

    /// "Transcribe Again" with engine and language (the engine applies to this run only).
    public void Retranscribe(string id, AsrEngine engine, TranscriptLanguage language) =>
        _services.Queue.Retranscribe(id, engine, language);

    /// Cancel: stops the running job and empties the queue (all back to `recorded`).
    public void CancelProcessing() => _services.Queue.Cancel();

    // ---- Models (spec 01 "Model download") ----

    /// The four core model folders are complete.
    [ObservableProperty] bool _modelsReady;
    /// Download progress while the models are being downloaded.
    [ObservableProperty] JobProgress? _modelSetup;

    void MarkModelsReady()
    {
        ModelsReady = true;
        _services.Settings.SetBool(SettingsStore.ModelsReadyKey, true);
    }

    /// Downloads every missing model (ignored while a download runs).
    public async void PrepareModels()
    {
        if (ModelSetup is not null) return;
        ModelSetup = new JobProgress(0, "Starting download");
        try
        {
            await Task.Run(() => ModelCatalog.DownloadAllAsync((f, m) => AppServices.Ui(() =>
            {
                if (ModelSetup is not null) ModelSetup = new JobProgress(f, m);
            })));
            ModelsReady = ModelCatalog.CoreModelsReady();
            if (ModelsReady) MarkModelsReady();
        }
        catch (Exception e)
        {
            ReportError($"Model download failed: {e.Message}");
        }
        ModelSetup = null;
    }

    // ---- Recorder settings ----

    /// Mode of the next (or running) recording; preselected from `defaultMode`.
    [ObservableProperty] MeetingMode _recordingMode;
    /// Title field of the recorder ("" = "Meeting <date> at <time>").
    [ObservableProperty] string _recordingTitle = "";
    /// Participants picker: 0 = detect automatically, else 2…10.
    [ObservableProperty] int _expectedSpeakers;
    /// Input devices for the Microphone picker.
    [ObservableProperty] IReadOnlyList<AudioInputDevice> _inputDevices = Array.Empty<AudioInputDevice>();

    /// Re-reads the input devices.
    public void RefreshDevices()
    {
        try { InputDevices = AudioDevices.InputDevices(); }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine($"Device list failed: {e.Message}"); InputDevices = Array.Empty<AudioInputDevice>(); }
    }

    // ---- Recording ----

    [ObservableProperty] bool _isRecording;
    /// Elapsed time in 0.5 s steps.
    [ObservableProperty] double _elapsed;
    /// Levels quantized to 2 dB steps.
    [ObservableProperty] float _micLevel;
    [ObservableProperty] float _systemLevel;
    /// Live lines of the running recording (full replacement list).
    [ObservableProperty] IReadOnlyList<LiveLine> _liveLines = Array.Empty<LiveLine>();
    /// The session being recorded (hidden from the list).
    [ObservableProperty] string? _recordingSessionId;

    /// Start/stop (⇧⌘R, tray, floating window, recorder button).
    public Task ToggleRecordingAsync() => IsRecording ? StopRecordingAsync() : StartRecordingAsync();

    /// Starts a Scribe recording with the recorder's settings.
    public async Task StartRecordingAsync() => await StartCoreAsync(RecordingMode, RecordingTitle, null, null, alert: true);

    /// <summary>
    /// Starts a recording for Assist: live transcription is on regardless of the preference, finished
    /// live lines go to <paramref name="liveLines"/>, <paramref name="stopped"/> gets the session id
    /// (empty on failure) when it ends, and the session is always queued for transcription.
    /// Returns the session id or the error text. A failed start is not shown in the global alert:
    /// Assist shows it in its own status line (spec 03 §5 start flow step 4).
    /// </summary>
    public async Task<(string? SessionId, string? Error)> StartRecordingAsync(MeetingMode mode, string title,
        Action<IReadOnlyList<LiveLine>> liveLines, Action<string> stopped)
    {
        if (IsRecording) return (null, "A recording is already running.");
        RecordingMode = mode;
        var error = await StartCoreAsync(mode, title, liveLines, stopped, alert: false);
        return error is null ? (RecordingSessionId, null) : (null, error);
    }

    /// Returns null on success, else the error (with <paramref name="alert"/> also reported through <see cref="Error"/>).
    async Task<string?> StartCoreAsync(MeetingMode mode, string title, Action<IReadOnlyList<LiveLine>>? hook, Action<string>? stopped,
                                       bool alert)
    {
        if (IsRecording) return "A recording is already running.";
        if (MicrophonePrivacy.Check() == MicrophoneAccess.Denied)
        {
            if (alert) ReportError(MicrophonePrivacy.DeniedMessage);
            return MicrophonePrivacy.DeniedMessage;
        }
        var prefs = _services.Preferences;
        SessionRecorder? rec = null;
        try
        {
            var t = title.Trim();
            rec = new SessionRecorder(mode, t.Length == 0 ? null : t,
                                      ExpectedSpeakers > 0 ? ExpectedSpeakers : null, prefs.SessionLanguage);
            LiveLines = Array.Empty<LiveLine>();
            Listen.RecordingWillStart();   // the recording takes over the microphone
            _forceLive = hook is not null;
            _liveLinesHook = hook;
            _onStopped = stopped;
            if ((prefs.LiveTranscription || Listen.IsListening || _forceLive) && ModelsReady)
                StartLive(rec, mode);
            IAudioSource mic = new WasapiMicSource(prefs.InputDeviceUID);
            IAudioSource? system = mode.RecordsSystemAudio() ? new WasapiSystemAudioSource() : null;
            await Task.Run(() => rec.Start(mic, system));
            rec.SourceFailed += (track, msg) => System.Diagnostics.Debug.WriteLine($"{track} source failed: {msg}");
            _recorder = rec;
            RecordingSessionId = rec.Info.Id;
            IsRecording = true;
            Elapsed = 0;
            _meterTimer = new DispatcherTimer(TimeSpan.FromSeconds(0.1), DispatcherPriority.Normal, (_, _) => UpdateMeters(),
                                              Application.Current.Dispatcher);
            _meterTimer.Start();
            SelectRequested?.Invoke(null);
            Reload();
            return null;
        }
        catch (Exception e)
        {
            var message = e is MicrophoneAccessDeniedException ? e.Message : $"Could not start recording: {e.Message}";
            if (alert) ReportError(message);
            await StopLiveAsync();
            _forceLive = false; _liveLinesHook = null; _onStopped = null;
            if (rec is not null)
            {
                rec.Dispose();
                // Nothing was recorded: remove the empty session folder again.
                try { Directory.Delete(AppPaths.Session(rec.Info.Id), true); } catch { }
            }
            Listen.RecordingDidStop();
            Reload();
            return message;
        }
    }

    void StartLive(SessionRecorder rec, MeetingMode mode)
    {
        try
        {
            var seg = Listen.BeginRecordingSegment(rec.Info.StartedAt.ToLocalTime(), mode);
            var lt = new LiveTranscriber(mode, lines => AppServices.Ui(() =>
            {
                Listen.Update(seg, lines);      // also the final lines drained after Stop
                if (_liveSegment != seg) return;
                LiveLines = lines;
                _liveLinesHook?.Invoke(lines);
            }));
            lt.Start(rec.Info.Tracks);
            rec.OnMicSamples = s => lt.Feed(Track.Mic, s);
            rec.OnSystemSamples = s => lt.Feed(Track.System, s);
            _live = lt;
            _liveSegment = seg;
        }
        catch (Exception e)
        {
            // The recording itself matters more than the preview.
            System.Diagnostics.Debug.WriteLine($"Live transcription unavailable: {e.Message}");
            _live = null;
        }
    }

    async Task StopLiveAsync()
    {
        var lt = _live;
        _live = null;
        if (lt is not null)
        {
            try { await lt.StopAsync(); } catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }
        }
        if (_liveSegment is { } seg) { seg.IsActive = false; Listen.Update(seg, seg.Lines); }
        _liveSegment = null;
    }

    void UpdateMeters()
    {
        if (_recorder is not { } r) return;
        var e = ScribeRules.PublishedElapsed(r.Elapsed);
        if (e != Elapsed) Elapsed = e;
        var m = LevelMath.Quantize(r.MicLevel);
        var s = LevelMath.Quantize(r.SystemLevel);
        if (m != MicLevel) MicLevel = m;
        if (s != SystemLevel) SystemLevel = s;
    }

    /// Stops the recording. With <paramref name="keepUntil"/> everything after that time is deleted (Stop Earlier).
    public async Task StopRecordingAsync(double? keepUntil = null)
    {
        if (_recorder is not { } rec) return;
        var assisted = _forceLive;
        var stopped = _onStopped;
        _forceLive = false; _liveLinesHook = null; _onStopped = null;
        _meterTimer?.Stop(); _meterTimer = null;
        _recorder = null;
        IsRecording = false;
        MicLevel = 0; SystemLevel = 0;
        RecordingSessionId = null;
        try
        {
            var info = await Task.Run(rec.Stop);
            rec.Dispose();
            await StopLiveAsync();
            Listen.RecordingDidStop();
            RecordingTitle = "";
            if (keepUntil is { } k && ScribeRules.ShouldTrimAfterStop(k, info.DurationSec))
            {
                try { SessionEdit.Trim(info.Id, 0, k); }
                catch (Exception e) { ReportError($"Could not remove the end: {e.Message}"); }
            }
            Reload();
            SelectRequested?.Invoke(info.Id);
            // Assist needs the final transcript for the recap.
            if (_services.Preferences.AutoTranscribe || assisted) Enqueue(info.Id);
            stopped?.Invoke(info.Id);
        }
        catch (Exception e)
        {
            ReportError($"Could not finish recording: {e.Message}");
            await StopLiveAsync();
            Listen.RecordingDidStop();
            Reload();
            stopped?.Invoke("");
        }
    }

    /// Quitting or shutting down: a running recording is finished synchronously so nothing is lost
    /// (status `recorded`, or `queued` with auto-transcribe so the next launch transcribes it).
    public void FinishForQuit()
    {
        if (_recorder is not { } rec) return;
        _recorder = null;
        _meterTimer?.Stop();
        try
        {
            var info = rec.Stop();
            rec.Dispose();
            if (_services.Preferences.AutoTranscribe || _forceLive) SessionStore.SetStatus(info.Id, SessionStatus.Queued);
            _onStopped?.Invoke(info.Id);
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine($"Finishing the recording failed: {e}"); }
        IsRecording = false;
    }

    // ---- Session management ----

    /// Deletes a session folder (the caller asks first).
    public void Delete(string id)
    {
        try { SessionStore.Delete(id); }
        catch (Exception e) { ReportError($"Could not delete: {e.Message}"); }
        Reload();
    }

    /// Renames a session.
    public void Rename(string id, string title)
    {
        var t = title.Trim();
        if (t.Length == 0) return;
        try { SessionStore.Rename(id, t); } catch (Exception e) { ReportError($"Could not rename: {e.Message}"); }
        Reload();
    }

    /// Trims a session to start…end.
    public void Trim(string id, double start, double end)
    {
        try { SessionEdit.Trim(id, start, end); }
        catch (Exception e) { ReportError($"Could not trim: {e.Message}"); }
        Reload();
    }

    /// Splits a session at a time; selects part 2.
    public void Split(string id, double time)
    {
        try
        {
            var second = SessionEdit.Split(id, time);
            Reload();
            SelectRequested?.Invoke(second.Id);
        }
        catch (Exception e) { ReportError($"Could not split: {e.Message}"); Reload(); }
    }

    /// Splits at a pause (optionally removing the pause from part 1); selects part 2.
    public void Split(string id, Pause pause, bool removePause)
    {
        try
        {
            var second = SessionEdit.Split(id, pause, removePause);
            Reload();
            SelectRequested?.Invoke(second.Id);
        }
        catch (Exception e) { ReportError($"Could not split: {e.Message}"); Reload(); }
    }

    /// Imports one file (In person) or two (Call: mic, then call audio), selects and queues the session.
    public async Task ImportFilesAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        try
        {
            var info = await Task.Run(() => SessionImport.ImportFiles(files[0], files.Count > 1 ? files[1] : null,
                                                                      SessionImport.ModeFor(files.Count)));
            Reload();
            SelectRequested?.Invoke(info.Id);
            Enqueue(info.Id);
        }
        catch (Exception e) { ReportError($"Import failed: {e.Message}"); }
    }

    /// Teaches the speaker library a name for a voice.
    public void LearnSpeaker(string name, float[] embedding) =>
        _services.UpdateSpeakerLibrary(lib => lib.Learn(name, embedding));
}
