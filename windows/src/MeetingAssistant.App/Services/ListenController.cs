using System.Windows;
using System.Windows.Threading;
using MeetingAssistant.Core.Capture;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Platform;
using MeetingAssistant.Core.Scribe;
using MeetingAssistant.Platform.Windows.Audio;
using MeetingAssistant.Platform.Windows.SystemServices;
using MeetingAssistant.Speech;

namespace MeetingAssistant.App.Services;

/// <summary>
/// Always-on live transcript for the floating window (spec 01 "Floating live transcript window"):
/// captures the mic (and call audio, depending on the recorder's mode) without saving anything,
/// rotates the live transcriber every ~10 minutes, and while a recording runs shows the recording's
/// own live transcript instead, so the window reads as one continuous transcript.
/// </summary>
public sealed class ListenController
{
    readonly ScribeController _scribe;
    readonly AppServices _services;
    readonly List<TranscriptSegment> _segments = new();
    readonly Dictionary<TranscriptSegment, LiveTranscriber> _transcribers = new();
    LiveCapture? _capture;
    TranscriptSegment? _current;
    DispatcherTimer? _timer, _rebuild;
    bool _suspended;
    DateTime _hiddenBefore = DateTime.MinValue;

    public ListenController(ScribeController scribe, AppServices services)
    {
        _scribe = scribe;
        _services = services;
    }

    /// The user wants listening on (window open and not paused).
    public bool IsListening { get; private set; }

    /// Error shown in orange in the window (null when fine).
    public string? Error { get; private set; }

    /// Mic / call-audio levels for the dots (raw RMS).
    public float MicLevel { get; private set; }
    public float SystemLevel { get; private set; }
    /// The current mode records call audio.
    public bool CapturesSystem => _scribe.RecordingMode.RecordsSystemAudio();

    /// Paragraphs (all of them; the window draws the last 150).
    public IReadOnlyList<PanelParagraph> Paragraphs { get; private set; } = Array.Empty<PanelParagraph>();

    /// Paragraphs, error, listening state or levels changed (UI thread).
    public event Action? Changed;
    public event Action? LevelsChanged;

    // ---- Listening ----

    /// Starts listening (window opened or resumed).
    public void StartListening()
    {
        if (IsListening) return;
        if (MicrophonePrivacy.Check() == MicrophoneAccess.Denied)
        {
            SetError("No microphone access. Allow it in Settings › Privacy & security › Microphone.");
            return;
        }
        if (!_scribe.ModelsReady) { SetError("Download the speech models first (main window)."); return; }
        Error = null;
        IsListening = true;
        // While a recording runs it owns the microphone; capture starts when it stops.
        if (_scribe.IsRecording) _suspended = true;
        else ResumeCapture();
        _timer ??= new DispatcherTimer(FloatingTranscript.Tick, DispatcherPriority.Background, (_, _) => Tick(), Application.Current.Dispatcher);
        _timer.Start();
        Changed?.Invoke();
    }

    /// Stops listening (window closed or paused).
    public void StopListening()
    {
        if (!IsListening) { if (Error is not null) { Error = null; Changed?.Invoke(); } return; }
        IsListening = false;
        _timer?.Stop();
        PauseCapture();
        MicLevel = 0; SystemLevel = 0;
        LevelsChanged?.Invoke();
        Changed?.Invoke();
    }

    void SetError(string message)
    {
        Error = message;
        IsListening = false;
        Changed?.Invoke();
    }

    /// A recording takes over the microphone; listening pauses until it ends.
    public void RecordingWillStart()
    {
        if (!IsListening) return;
        _suspended = true;
        PauseCapture();
    }

    /// The recording ended; listening resumes if it was paused for it.
    public void RecordingDidStop()
    {
        if (!_suspended) return;
        _suspended = false;
        if (IsListening) ResumeCapture();
    }

    /// The mode changed on the recorder: restart capture so call audio follows it.
    public void ModeChanged()
    {
        if (!IsListening || _scribe.IsRecording || _capture is null) return;
        PauseCapture();
        ResumeCapture();
    }

    void ResumeCapture()
    {
        if (_capture is { IsRunning: true }) return;
        try
        {
            // Never hold a Bluetooth headset's mic open: it would switch the headphones to call quality.
            var device = AudioDevices.NonBluetoothInput(_services.Preferences.InputDeviceUID);
            var capture = new LiveCapture();
            IAudioSource? system = CapturesSystem ? new WasapiSystemAudioSource() : null;
            capture.Start(new WasapiMicSource(device), system);
            _capture = capture;
            BeginSegment();
        }
        catch (Exception e)
        {
            Error = e is MicrophoneAccessDeniedException
                ? "No microphone access. Allow it in Settings › Privacy & security › Microphone."
                : $"Could not listen: {e.Message}";
            IsListening = false;
            _timer?.Stop();
            Changed?.Invoke();
        }
    }

    void PauseCapture()
    {
        _capture?.Stop();
        _capture = null;
        if (_current is { } seg) { _current = null; _ = FinishAsync(seg); }
    }

    void BeginSegment()
    {
        if (_capture is null) return;
        var mode = _scribe.RecordingMode;
        var seg = new TranscriptSegment(DateTime.Now, mode, false);
        LiveTranscriber lt;
        try
        {
            lt = new LiveTranscriber(mode, lines => AppServices.Ui(() => { seg.Lines = lines; ScheduleRebuild(); }));
            lt.Start(mode.RecordsSystemAudio() ? new[] { Track.Mic, Track.System } : new[] { Track.Mic });
        }
        catch (Exception e)
        {
            Error = $"Could not listen: {e.Message}";
            Changed?.Invoke();
            return;
        }
        _transcribers[seg] = lt;
        var old = _current;
        _current = seg;
        _segments.Add(seg);
        _capture.BeginSegment(s => lt.Feed(Track.Mic, s), s => lt.Feed(Track.System, s));
        if (old is not null) _ = FinishAsync(old);
    }

    async Task FinishAsync(TranscriptSegment seg)
    {
        if (_transcribers.Remove(seg, out var lt))
        {
            try { await lt.StopAsync(); } catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }
        }
        seg.IsActive = false;   // frees audio buffers and diarizer state with the transcriber
        FloatingTranscript.Trim(_segments, _current);
        ScheduleRebuild();
    }

    void Tick()
    {
        var rec = _scribe.IsRecording;
        MicLevel = rec ? _scribe.MicLevel : _capture?.MicLevel ?? 0;
        SystemLevel = rec ? _scribe.SystemLevel : _capture?.SystemLevel ?? 0;
        LevelsChanged?.Invoke();
        // Start a fresh transcriber now and then, preferably in a pause.
        if (_current is { } seg && FloatingTranscript.ShouldRotate(seg, DateTime.Now)) BeginSegment();
    }

    // ---- Recording segments ----

    /// A recording started: its live lines are shown in the same list (marked as recorded).
    public TranscriptSegment BeginRecordingSegment(DateTime start, MeetingMode mode)
    {
        var seg = new TranscriptSegment(start, mode, true);
        _segments.Add(seg);
        return seg;
    }

    /// New lines of a recording segment.
    public void Update(TranscriptSegment seg, IReadOnlyList<LiveLine> lines)
    {
        seg.Lines = lines;
        ScheduleRebuild();
    }

    /// Coalesce bursts of updates (two tracks, speaker labels) into one redraw every 0.4 s.
    void ScheduleRebuild()
    {
        if (_rebuild is { IsEnabled: true }) return;
        _rebuild ??= new DispatcherTimer(FloatingTranscript.Coalesce, DispatcherPriority.Background, (_, _) =>
        {
            _rebuild!.Stop();
            Rebuild();
        }, Application.Current.Dispatcher);
        _rebuild.Start();
    }

    // ---- Output ----

    /// Clear: hides everything before now and drops finished segments.
    public void Clear()
    {
        _segments.RemoveAll(s => s != _current && !s.IsActive && !(s.IsRecording && _scribe.IsRecording));
        _hiddenBefore = DateTime.Now;
        Rebuild();
    }

    /// Copy Transcript text.
    public string PlainText => FloatingTranscript.PlainText(Paragraphs);

    void Rebuild()
    {
        Paragraphs = FloatingTranscript.Build(_segments, _services.Preferences.MyName, _hiddenBefore);
        Changed?.Invoke();
    }

    /// Stops everything (app exit).
    public void Shutdown()
    {
        _timer?.Stop();
        _capture?.Stop();
        _capture = null;
        foreach (var lt in _transcribers.Values) _ = lt.StopAsync();
        _transcribers.Clear();
    }
}
