namespace MeetingAssistant.Core.Claude;

/// <summary>
/// One prompt, one answer over a freshly started stream process (the process lifecycle of Scribe+,
/// spec 04 §2.3). Starting <c>claude</c> takes about two seconds, so the process is started as soon as
/// the shortcut is pressed and waits while the user speaks. Each dictation gets a fresh process, so
/// nothing of one dictation leaks into the next. The prompt text itself belongs to the Scribe+ code.
/// </summary>
public sealed class ClaudeRewriteSession : IDisposable
{
    /// <summary>Default answer timeout in seconds, counted from sending the prompt (spec 04 §2.3).</summary>
    public const double DefaultTimeoutSeconds = 8;

    readonly IClaudeConversation _stream;
    readonly object _lock = new();
    readonly System.Text.StringBuilder _text = new();
    readonly TaskCompletionSource<string> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool _sent;

    /// <summary>The model the process runs (a spare is only reused for the same model).</summary>
    public string Model { get; }
    /// <summary>When the process was started (a spare older than 30 minutes is replaced).</summary>
    public DateTime StartedAt { get; }

    /// <summary>Starts the process. Throws <see cref="ClaudeException"/> when the binary cannot be launched.</summary>
    public ClaudeRewriteSession(string binary, string system, string model, string effort = "low",
                                ClaudeConversationFactory? factory = null, DateTime? now = null)
    {
        Model = model;
        StartedAt = now ?? DateTime.UtcNow;
        _stream = (factory ?? ClaudeStream.Factory(binary))(system, model, effort, Handle);
        _stream.Start();
    }

    /// <summary>Sends the prompt and waits for the answer: the result text, or the streamed text when it is empty.</summary>
    public async Task<string> RewriteAsync(string prompt, double timeoutSeconds = DefaultTimeoutSeconds)
    {
        lock (_lock) _sent = true;
        try { _stream.Send(prompt); }
        catch (Exception e) { Finish(null, e); }
        var done = await Task.WhenAny(_result.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        if (done != _result.Task) Finish(null, ClaudeException.Failed("Claude took too long"));
        try { return await _result.Task; }
        finally { _stream.Stop(); }
    }

    /// <summary>Still running and unused.</summary>
    public bool IsUsable
    {
        get { lock (_lock) return !_sent && !_result.Task.IsCompleted && _stream.IsRunning; }
    }

    /// <summary>Stops the process.</summary>
    public void Close() => _stream.Stop();
    public void Dispose() => Close();

    void Handle(ClaudeStreamEvent ev)
    {
        switch (ev)
        {
            case ClaudeStreamEvent.TextDelta t:
                lock (_lock) _text.Append(t.Text);
                break;
            case ClaudeStreamEvent.TurnDone d:
                if (d.Result.IsError)
                    Finish(null, ClaudeCli.IsUsageLimit(d.Result.Text) ? ClaudeException.UsageLimit(d.Result.Text) : ClaudeException.Failed(d.Result.Text));
                else
                {
                    string t;
                    lock (_lock) t = d.Result.Text.Length == 0 ? _text.ToString() : d.Result.Text;
                    Finish(t, null);
                }
                _stream.Stop();
                break;
            case ClaudeStreamEvent.Exited x:
                var e = x.Stderr.ToLowerInvariant();
                var tail = x.Stderr.Length > 200 ? x.Stderr[^200..] : x.Stderr;
                Finish(null, e.Contains("login") || e.Contains("auth") ? ClaudeException.NotLoggedIn()
                                 : ClaudeException.Failed($"claude exited ({x.Status}) {tail}"));
                break;
        }
    }

    void Finish(string? text, Exception? error)
    {
        if (error is not null) _result.TrySetException(error); else _result.TrySetResult(text ?? "");
    }
}

/// <summary>
/// Keeps one started Claude process ready for the next Scribe+ dictation ("Keep Claude ready",
/// spec 04 §2.3). The caller decides when to check (3 s after a settings change or app start, 1 s
/// after a dictation ends, every 10 minutes); this class applies the reuse rules.
/// </summary>
public sealed class ClaudeSpare : IDisposable
{
    /// <summary>A spare older than this is replaced, so Claude Code updates are picked up.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
    /// <summary>How often the owner should re-check the spare.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);

    readonly Func<string, ClaudeRewriteSession> _start;
    readonly Func<DateTime> _now;
    readonly object _lock = new();
    ClaudeRewriteSession? _spare;

    /// <param name="start">Starts a session for a model.</param>
    public ClaudeSpare(Func<string, ClaudeRewriteSession> start, Func<DateTime>? now = null)
    {
        _start = start;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>True when the session can be used for <paramref name="model"/> now.</summary>
    public bool Fits(ClaudeRewriteSession s, string model) =>
        s.IsUsable && s.Model == model && _now() - s.StartedAt < MaxAge;

    /// <summary>
    /// Keeps a spare when <paramref name="wanted"/> (dictation on, a Scribe+ shortcut set, the option on);
    /// otherwise closes it. A spare that exited, was used, has another model or is too old is replaced.
    /// </summary>
    public void Refresh(bool wanted, string model)
    {
        ClaudeRewriteSession? close = null;
        lock (_lock)
        {
            if (!wanted) { close = _spare; _spare = null; }
            else if (_spare is null || !Fits(_spare, model))
            {
                close = _spare;
                _spare = null;
                try { _spare = _start(model); } catch { _spare = null; }
            }
        }
        close?.Close();
    }

    /// <summary>On a Scribe+ press: the spare when it fits, else a newly started session.</summary>
    public ClaudeRewriteSession Take(string model)
    {
        ClaudeRewriteSession? s;
        lock (_lock) { s = _spare; _spare = null; }
        if (s is not null && Fits(s, model)) return s;
        s?.Close();
        return _start(model);
    }

    public void Dispose()
    {
        lock (_lock) { _spare?.Close(); _spare = null; }
    }
}
