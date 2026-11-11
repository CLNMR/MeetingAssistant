using MeetingAssistant.Core;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;

namespace MeetingAssistant.Speech;

/// The serial transcription queue (spec 01-recording, "Transcription queue and session
/// statuses"): one FIFO, a session is never queued twice; enqueue sets `queued`, the worker sets
/// `transcribing`, then `done` or `failed`; Cancel stops the running job and empties the queue
/// (all back to `recorded`). When the queue runs empty the final-pass models are unloaded.
/// "Transcribe Again" with an engine applies that engine to one run only.
public sealed class TranscriptionQueue
{
    readonly object _lock = new();
    readonly List<string> _queue = new();
    readonly Dictionary<string, AsrEngine> _engineOverride = new();
    readonly Func<PipelineSettings> _settings;
    readonly Func<ITranscriptTextCleanup?> _cleanup;
    readonly Func<string, PipelineSettings, ITranscriptTextCleanup?, Action<double, string>, CancellationToken, Transcript> _run;
    Task? _worker;
    CancellationTokenSource? _cts;
    string? _current;

    /// `settings` gives the current preferences (engine, language, myName) for each job;
    /// `cleanup` the transcript cleanup (credits + vocabulary). `run` defaults to the real pipeline
    /// (tests pass a fake).
    public TranscriptionQueue(Func<PipelineSettings> settings, Func<ITranscriptTextCleanup?>? cleanup = null,
                              Func<string, PipelineSettings, ITranscriptTextCleanup?, Action<double, string>, CancellationToken, Transcript>? run = null)
    {
        _settings = settings;
        _cleanup = cleanup ?? (() => null);
        _run = run ?? ((id, s, c, p, ct) => TranscriptionPipeline.Run(id, s, c, p, ct));
    }

    /// A session's status was written to `session.json`.
    public event Action<string, SessionStatus>? StatusChanged;
    /// Progress of the running job: (session id, fraction 0…1, message).
    public event Action<string, double, string>? Progress;
    /// A job finished successfully (the app plays a sound / flashes the window).
    public event Action<string, Transcript>? Finished;
    /// A job threw; the app shows "Transcription failed: <message>".
    public event Action<string, Exception>? Failed;
    /// The queue ran empty (models were unloaded).
    public event Action? Idle;

    /// Called when the queue runs empty; default unloads the final-pass diarizer.
    public Action UnloadModels { get; set; } = () => SpeechEngine.Shared.UnloadTranscriptionModels();

    /// Session ids waiting (not including the running one).
    public IReadOnlyList<string> Waiting { get { lock (_lock) return _queue.ToList(); } }
    /// The running session, if any.
    public string? Current { get { lock (_lock) return _current; } }
    public bool IsBusy { get { lock (_lock) return _worker is not null; } }

    /// Queue a session (ignored when already waiting or running) and start the worker.
    public void Enqueue(string id)
    {
        lock (_lock)
        {
            if (_queue.Contains(id) || _current == id) return;
            _queue.Add(id);
        }
        SetStatus(id, SessionStatus.Queued);
        lock (_lock)
        {
            if (_worker is not null) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _worker = Task.Run(() => Drain(ct));
        }
    }

    /// "Transcribe Again" with engine and language: the language is stored in the session
    /// (`auto` removes it), the engine is used for this run only.
    public void Retranscribe(string id, AsrEngine engine, TranscriptLanguage language)
    {
        if (!JsonIO.TryRead<SessionInfo>(AppPaths.Info(id), out var info) || info is null) return;
        info.Language = language == TranscriptLanguage.Auto ? null : language.Raw();
        JsonIO.Write(info, AppPaths.Info(id));
        lock (_lock) _engineOverride[id] = engine;
        Enqueue(id);
    }

    /// Cancel the running job and empty the queue: every affected session goes back to `recorded`.
    public void Cancel()
    {
        List<string> dropped;
        lock (_lock)
        {
            _cts?.Cancel();
            dropped = _queue.ToList();
            _queue.Clear();
            _engineOverride.Clear();
        }
        foreach (var id in dropped) SetStatus(id, SessionStatus.Recorded);
    }

    /// Wait until the queue is empty (msctl, tests).
    public Task WhenIdle() { lock (_lock) return _worker ?? Task.CompletedTask; }

    void Drain(CancellationToken ct)
    {
        while (true)
        {
            string id;
            AsrEngine? engine = null;
            lock (_lock)
            {
                if (_queue.Count == 0 || ct.IsCancellationRequested)
                {
                    // Cleared in the same lock as the emptiness check, so a concurrent Enqueue starts a new worker.
                    _current = null; _worker = null; _cts = null;
                    break;
                }
                id = _queue[0];
                _queue.RemoveAt(0);
                _current = id;
                if (_engineOverride.Remove(id, out var e)) engine = e;
            }
            SetStatus(id, SessionStatus.Transcribing);
            Progress?.Invoke(id, 0, "Starting");
            try
            {
                var settings = _settings();
                if (engine is { } en) settings = settings with { Engine = en };
                var t = _run(id, settings, _cleanup(), (p, m) => Progress?.Invoke(id, p, m), ct);
                SetStatus(id, SessionStatus.Done);
                Finished?.Invoke(id, t);
            }
            catch (OperationCanceledException)
            {
                SetStatus(id, SessionStatus.Recorded);
            }
            catch (Exception e)
            {
                SetStatus(id, SessionStatus.Failed);
                Failed?.Invoke(id, e);
            }
        }
        try { UnloadModels(); } catch { }
        Idle?.Invoke();
    }

    /// Writes a status into `session.json` (replaceable for tests).
    public Action<string, SessionStatus> WriteStatus { get; set; } = (id, status) =>
    {
        if (!JsonIO.TryRead<SessionInfo>(AppPaths.Info(id), out var info) || info is null) return;
        info.Status = status;
        JsonIO.Write(info, AppPaths.Info(id));
    };

    void SetStatus(string id, SessionStatus status)
    {
        WriteStatus(id, status);
        StatusChanged?.Invoke(id, status);
    }
}
