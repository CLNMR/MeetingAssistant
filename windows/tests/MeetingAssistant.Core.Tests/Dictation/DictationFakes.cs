using MeetingAssistant.Core.Claude;
using MeetingAssistant.Core.Dictation;
using MeetingAssistant.Core.Input;
using MeetingAssistant.Core.Platform;
using MeetingAssistant.Core.Storage;
using MeetingAssistant.Core.Text;

namespace MeetingAssistant.Core.Tests.Dictation;

/// A clock whose delays complete only when the test advances it; continuations run inline.
public sealed class FakeDictationClock : IDictationClock
{
    sealed record Pending(DateTime Due, TaskCompletionSource Tcs);
    readonly List<Pending> _pending = new();

    public DateTime UtcNow { get; private set; } = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    public Task Delay(TimeSpan delay, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        if (delay <= TimeSpan.Zero) return Task.CompletedTask;
        var tcs = new TaskCompletionSource();
        var p = new Pending(UtcNow + delay, tcs);
        lock (_pending) _pending.Add(p);
        if (ct.CanBeCanceled) ct.Register(() => { lock (_pending) _pending.Remove(p); tcs.TrySetCanceled(ct); });
        return tcs.Task;
    }

    /// Moves time forward, firing every delay due on the way in order (also those started meanwhile).
    public void Advance(TimeSpan by)
    {
        var target = UtcNow + by;
        while (true)
        {
            Pending? next;
            lock (_pending)
            {
                next = _pending.Where(p => p.Due <= target).OrderBy(p => p.Due).FirstOrDefault();
                if (next is not null) _pending.Remove(next);
            }
            if (next is null) break;
            if (next.Due > UtcNow) UtcNow = next.Due;
            next.Tcs.TrySetResult();
        }
        UtcNow = target;
    }

    public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}

/// A microphone the test feeds by hand.
public sealed class FakeMicrophone : IDictationMicrophone
{
    public MicrophonePermission Permission { get; set; } = MicrophonePermission.Granted;
    public int PermissionRequests { get; private set; }
    public Exception? FailStart { get; set; }
    public FakeSource? Current { get; private set; }
    public int Created { get; private set; }

    public void RequestPermission() => PermissionRequests++;

    public IAudioSource Create()
    {
        Created++;
        Current = new FakeSource(FailStart);
        return Current;
    }

    public sealed class FakeSource(Exception? failStart) : IAudioSource
    {
        public event Action<float[]>? Samples;
        public event Action<string>? Failed;
        public bool Running { get; private set; }
        public bool Stopped { get; private set; }
        public void Start() { if (failStart is not null) throw failStart; Running = true; }
        public void Stop() { Running = false; Stopped = true; }
        public void Push(float[] s) { if (Running) Samples?.Invoke(s); }
        public void Fail(string m) => Failed?.Invoke(m);
        public void Dispose() => Stop();
    }
}

/// Recognizer returning text from a function; records every clip.
public sealed class FakeRecognizer : IDictationRecognizer
{
    public Func<float[], string> Text { get; set; } = _ => "";
    public Exception? Fail { get; set; }
    public List<float[]> Calls { get; } = new();

    public Task<string> DictateAsync(float[] samples, CancellationToken ct = default)
    {
        Calls.Add(samples);
        if (Fail is not null) return Task.FromException<string>(Fail);
        return Task.FromResult(Text(samples));
    }
}

public sealed class FakeFields : IFocusedFieldSource
{
    public static readonly FieldIdentity Editor = new(100, new[] { 42, 1 }, 50004, (10, 20));
    public static readonly FieldIdentity OtherField = new(100, new[] { 42, 2 }, 50004, (10, 300));

    /// Identity returned now (change it to simulate a click into another field).
    public FieldIdentity Identity { get; set; } = Editor;
    public string? App { get; set; } = "Mail";
    public string? Before { get; set; }
    public string? After { get; set; }
    public List<bool> Reads { get; } = new();

    public Task<DictationField?> ReadAsync(bool readText)
    {
        Reads.Add(readText);
        return Task.FromResult<DictationField?>(readText
            ? new DictationField(App, Identity, Before, After)
            : new DictationField(App, Identity));
    }
}

public sealed class FakeOutput : IDictationOutput
{
    public List<string> Pasted { get; } = new();
    public List<string> Copied { get; } = new();
    public PasteResult Result { get; set; } = PasteResult.Ok;

    public Task<PasteResult> PasteAsync(string text)
    {
        if (Result.Delivered) Pasted.Add(text);
        return Task.FromResult(Result);
    }

    public bool Copy(string text) { Copied.Add(text); return true; }
}

public sealed class FakeMuter : IOutputMuter
{
    public int Mutes, Restores;
    public void Mute() => Mutes++;
    public void Restore() => Restores++;
}

/// A scripted Claude process for Scribe+.
public sealed class FakeClaude : IClaudeConversation
{
    public sealed class Log
    {
        public List<FakeClaude> All { get; } = new();
        public bool FailStart { get; set; }
        /// What a process does when the prompt arrives (null = never answers).
        public Action<FakeClaude, string>? OnSend { get; set; } = (c, _) => c.Answer("Fertig.");
    }

    readonly Log _log;
    readonly Action<ClaudeStreamEvent> _onEvent;
    public string Model { get; }
    public List<string> Sent { get; } = new();
    public bool Running { get; private set; }
    public bool Stopped { get; private set; }

    FakeClaude(Log log, string model, Action<ClaudeStreamEvent> onEvent) { _log = log; Model = model; _onEvent = onEvent; }

    public static ClaudeConversationFactory Factory(Log log) => (_, model, _, onEvent) =>
    {
        var c = new FakeClaude(log, model, onEvent);
        log.All.Add(c);
        return c;
    };

    public bool IsRunning => Running;
    public void Start() { if (_log.FailStart) throw ClaudeException.NotInstalled(); Running = true; }
    public void Send(string text) { Sent.Add(text); _log.OnSend?.Invoke(this, text); }
    public void Stop() { Stopped = true; Running = false; }

    public void Answer(string text) => _onEvent(new ClaudeStreamEvent.TurnDone(new ClaudeTurnResult(false, text, 0.001, 100, 900)));
    public void Error(string text) => _onEvent(new ClaudeStreamEvent.TurnDone(new ClaudeTurnResult(true, text, 0, 0, 0)));
    public void Exit(int status, string stderr) => _onEvent(new ClaudeStreamEvent.Exited(status, stderr));
}

/// Everything a controller test needs, wired together.
public sealed class DictationRig : IDisposable
{
    public FakeDictationClock Clock { get; } = new();
    public FakeMicrophone Mic { get; } = new();
    public FakeRecognizer Asr { get; } = new();
    public FakeFields Fields { get; } = new();
    public FakeOutput Output { get; } = new();
    public FakeMuter Muter { get; } = new();
    public FakeClaude.Log Claude { get; } = new();
    /// Preferences; the spare Claude process is off unless a test switches it on.
    public Preferences Prefs { get; } = new() { ScribePlusWarm = false };
    public bool Recording { get; set; }
    public bool NoClaudeBinary { get; set; }
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "ma-dictation-" + Guid.NewGuid().ToString("N"));
    public List<string> Logs { get; } = new();
    public DictationController C { get; }

    public DictationRig()
    {
        C = new DictationController(Mic, Asr, Fields, Output,
            model => NoClaudeBinary
                ? throw ClaudeException.NotInstalled()
                : new ClaudeRewriteSession("claude", ScribePlus.System, model, ScribePlus.Effort, FakeClaude.Factory(Claude), Clock.UtcNow),
            () => Prefs, Muter, Clock, new DictationRecoveryStore(Folder), () => Recording);
        C.Log += Logs.Add;
    }

    public string[] KeptFiles => Directory.Exists(Folder) ? Directory.GetFiles(Folder) : Array.Empty<string>();

    /// Speech-like audio: a tone with the given seconds of silence before and after.
    public static float[] Speech(double seconds, double silenceBefore = 0.5, double silenceAfter = 0.5, float amplitude = 0.2f)
    {
        int pre = (int)(silenceBefore * 16_000), n = (int)(seconds * 16_000), post = (int)(silenceAfter * 16_000);
        var s = new float[pre + n + post];
        for (int i = 0; i < n; i++) s[pre + i] = amplitude * MathF.Sin(2 * MathF.PI * 220 * i / 16_000f);
        return s;
    }

    public static float[] Silence(double seconds) => new float[(int)(seconds * 16_000)];

    /// Press, feed audio while the clock runs at the audio's pace, release; returns the release task.
    public Task Dictate(float[] audio, DictationMode mode = DictationMode.Plain, double extraHold = 0)
    {
        C.Press(mode);
        Feed(audio);
        if (extraHold > 0) Clock.Advance(extraHold);
        var t = C.ReleaseAsync();
        Clock.Advance(0.2);
        return t;
    }

    /// Pushes audio in 100 ms blocks, advancing the clock with it.
    public void Feed(float[] audio)
    {
        for (int pos = 0; pos < audio.Length; pos += 1600)
        {
            Mic.Current?.Push(audio.AsSpan(pos, Math.Min(1600, audio.Length - pos)).ToArray());
            Clock.Advance(0.1);
        }
    }

    public void Dispose()
    {
        C.Dispose();
        try { Directory.Delete(Folder, true); } catch { }
    }
}
