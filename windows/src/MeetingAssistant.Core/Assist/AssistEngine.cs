using System.Threading.Channels;
using MeetingAssistant.Core.Claude;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Assist;

/// <summary>
/// The live assistant (spec 02 §2): turns live transcript lines into Claude updates over one long-running
/// conversation and applies Claude's ops while they stream. All work runs one item at a time on an internal
/// queue (like the Mac's actor); public methods may be called from any thread.
/// </summary>
public sealed class AssistEngine
{
    /// <summary>Called after every state change with a copy of the state and the status.</summary>
    public delegate void PublishHandler(AssistState state, AssistStatus status);

    /// <summary>Tick interval (spec 02 §2.4).</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.5);
    /// <summary>An update without an answer for this long restarts the conversation ("no answer").</summary>
    public const double HangSeconds = 90;
    /// <summary>Retry delay after a usage-limit answer.</summary>
    public const double LimitRetrySeconds = 300;
    /// <summary>A question from the other side is answered after this many seconds.</summary>
    public const double QuestionDelaySeconds = 6;
    /// <summary>The resume message carries the transcript of this many seconds.</summary>
    public const double ResumeWindowSeconds = 300;
    /// <summary>After this many unexpected exits in a row the engine gives up.</summary>
    public const int MaxRestarts = 5;
    /// <summary>Wait before restarting after an unexpected exit.</summary>
    public static readonly TimeSpan ExitRestartDelay = TimeSpan.FromSeconds(2);

    const string SuggestEvent = "user asks: suggest a question now";

    readonly AssistContext _context;
    readonly AssistSettings _settings;
    readonly ClaudeConversationFactory _factory;
    readonly PublishHandler _publish;
    readonly TimeProvider _time;
    readonly (string Yes, string No) _yesNo;
    readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });

    AssistState _state = new();
    volatile AssistStatus _status = new AssistStatus.Idle();

    // Conversation
    IClaudeConversation? _stream;
    int _generation;
    DateTimeOffset _conversationStart;
    int _lastContextTokens;
    double _costBase;                 // cost of earlier conversations (restarts)
    double _costThisConversation;
    bool _busy;
    DateTimeOffset _lastSend = DateTimeOffset.MinValue;
    string _outputBuffer = "";
    int _restarts;
    bool _stopping;
    DateTimeOffset? _limitedUntil;
    bool _pendingRestart;
    volatile bool _paused;
    CancellationTokenSource? _loop;

    // Transcript
    readonly HashSet<string> _sentUtterances = new();
    readonly Dictionary<string, DateTimeOffset> _finalSeen = new();
    readonly Dictionary<string, string> _speakerNames = new();
    List<AssistLine> _pending = new();
    readonly List<AssistLine> _history = new();
    List<string> _events = new();
    /// Events sent with the update still waiting for its answer; carried into a new conversation on restart.
    List<string> _unansweredEvents = new();
    bool _forceNow;

    // Timing measurements (msctl replay)
    readonly List<(double? FirstOp, double Total)> _latencies = new();
    DateTimeOffset? _firstOpAt;

    public AssistEngine(AssistContext context, AssistSettings settings, ClaudeConversationFactory factory, PublishHandler publish,
                        TimeProvider? time = null)
    {
        _context = context;
        _settings = settings;
        _factory = factory;
        _publish = publish;
        _time = time ?? TimeProvider.System;
        _conversationStart = Now;
        _state.Playbook = context.PlaybookItems.Select(p => p with { }).ToList();
        _state.Tables = context.TemplateTables?.Select(t => System.Text.Json.JsonSerializer.Deserialize<TemplateTable>(
            System.Text.Json.JsonSerializer.Serialize(t, Json.JsonIO.Options), Json.JsonIO.Options)!).ToList();
        _yesNo = context.YesNoWords;
        // Attendees start empty; Claude names them (known contacts reach it only through the context message).
        _state.Attendees = new();
        _ = Task.Run(RunQueueAsync);
    }

    /// <summary>An engine on the real <c>claude</c> binary.</summary>
    public AssistEngine(AssistContext context, AssistSettings settings, string binary, PublishHandler publish)
        : this(context, settings, ClaudeStream.Factory(binary), publish) { }

    DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>The last published status (thread-safe).</summary>
    public AssistStatus Status => _status;
    public bool Paused => _paused;

    // MARK: Queue

    async Task RunQueueAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync())
        {
            try { await work(); } catch { /* one failed item must not stop the engine */ }
        }
    }

    Task Do(Action a) => Do(() => { a(); return 0; });

    Task<T> Do<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(() =>
            {
                try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); }
                return Task.CompletedTask;
            }))
            tcs.SetException(new InvalidOperationException("engine stopped"));
        return tcs.Task;
    }

    void Post(Action a) => _queue.Writer.TryWrite(() => { a(); return Task.CompletedTask; });

    // MARK: Lifecycle

    /// <summary>Opens the conversation with the context message and starts the 0.5 s loop (spec 02 §2.5).</summary>
    public Task StartAsync(bool runLoop = true) => Do(() =>
    {
        if (_stream is not null) return;
        _stopping = false;
        SetStatus(new AssistStatus.Starting());
        OpenConversation(AssistPrompts.ContextMessage(_context));
        if (!runLoop) return;
        _loop = new CancellationTokenSource();
        var ct = _loop.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TickInterval, _time, ct); } catch (OperationCanceledException) { return; }
                await Do(Tick);
            }
        }, ct);
    });

    /// <summary>Stops the loop and the process and returns the final state (status stopped).</summary>
    public Task<AssistState> StopAsync() => Do(() =>
    {
        _stopping = true;
        _loop?.Cancel(); _loop = null;
        _stream?.Stop(); _stream = null;
        SetStatus(new AssistStatus.Stopped());
        return _state.Clone();
    });

    void OpenConversation(string first)
    {
        _generation += 1;
        var gen = _generation;
        try
        {
            var s = _factory(AssistPrompts.System, _settings.Model, _settings.Effort, ev => Post(() => Handle(ev, gen)));
            s.Start();
            _stream = s;
            _conversationStart = Now;
            _costBase += _costThisConversation;
            _costThisConversation = 0;
            _lastContextTokens = 0;
            Send(first);
        }
        catch (Exception e)
        {
            SetStatus(new AssistStatus.Failed(e.Message));
        }
    }

    void Send(string text)
    {
        if (_stream is not { } s) return;
        _busy = true;
        _lastSend = Now;
        _firstOpAt = null;
        _outputBuffer = "";
        s.Send(text);
        SetStatus(new AssistStatus.Thinking());
    }

    // MARK: Input

    /// <summary>
    /// Feeds the full current list of live lines (spec 02 §2.3). Finished utterances are sent once, when all
    /// their speaker labels are known or after <see cref="AssistSettings.LabelWait"/>.
    /// </summary>
    public Task IngestAsync(IReadOnlyList<LiveLine> lines) => Do(() => Ingest(lines));

    void Ingest(IReadOnlyList<LiveLine> lines)
    {
        var now = Now;
        var groups = new Dictionary<string, List<LiveLine>>();
        var order = new List<string>();
        foreach (var l in lines)
        {
            var parts = l.Id.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var utt = parts.Length > 1 ? string.Join(".", parts[..^1]) : "";
            if (utt.Length == 0) utt = l.Id;
            if (!groups.TryGetValue(utt, out var g)) { groups[utt] = g = new(); order.Add(utt); }
            g.Add(l);
        }
        var ready = new List<(string Utt, List<LiveLine> Lines)>();
        foreach (var utt in order)
        {
            var ls = groups[utt];
            if (_sentUtterances.Contains(utt) || !ls.All(x => x.IsFinal)) continue;
            var seen = _finalSeen.TryGetValue(utt, out var s) ? s : now;
            _finalSeen[utt] = seen;
            var labelled = ls.All(x => x.Speaker is not null);
            if (labelled || (now - seen).TotalSeconds >= _settings.LabelWait) ready.Add((utt, ls));
        }
        foreach (var (utt, ls) in ready.OrderBy(r => r.Lines.Count > 0 ? r.Lines[0].Start : 0))
        {
            _sentUtterances.Add(utt);
            _finalSeen.Remove(utt);
            foreach (var l in ls.OrderBy(x => x.Start))
            {
                var text = AssistText.TrimWs(l.Text);
                if (text.Length == 0) continue;
                _pending.Add(new AssistLine(l.Start, SpeakerName(l), text));
            }
        }
    }

    /// <summary>Lines from a finished transcript (replays and tests) go straight to pending.</summary>
    public Task IngestLinesAsync(IEnumerable<AssistLine> lines) => Do(() => _pending.AddRange(lines));

    string MyName => _context.MyName.Length == 0 ? "Me" : _context.MyName;

    /// The display name of a raw live label (spec 02 §2.3).
    string SpeakerName(LiveLine l)
    {
        if (l.Speaker is not { } raw) return _context.Mode == MeetingMode.Call && l.Track == Track.System ? "Them (?)" : "?";
        if (raw == "me") return MyName;
        if (_speakerNames.TryGetValue(raw, out var n)) return n;
        n = $"Speaker {_speakerNames.Count + 1}";
        _speakerNames[raw] = n;
        _state.SpeakerNames = new Dictionary<string, string>(_speakerNames);
        return n;
    }

    /// <summary>
    /// The user named a speaker of the live transcript (spec 02 §5.1): earlier lines are relabelled, Claude is
    /// told, and the person becomes a present attendee.
    /// </summary>
    public Task RenameSpeakerAsync(string raw, string name) => Do(() =>
    {
        name = AssistText.TrimAll(name);
        if (name.Length == 0) return;
        string? old = _speakerNames.TryGetValue(raw, out var o) ? o : raw == "me" ? MyName : null;
        if (old == name) return;
        _speakerNames[raw] = name;
        _state.SpeakerNames = new Dictionary<string, string>(_speakerNames);
        if (old is not null)
        {
            for (int i = 0; i < _history.Count; i++) if (_history[i].Speaker == old) _history[i] = _history[i] with { Speaker = name };
            for (int i = 0; i < _pending.Count; i++) if (_pending[i].Speaker == old) _pending[i] = _pending[i] with { Speaker = name };
            _events.Add($"user: the speaker labelled \"{old}\" is {name}. Earlier lines of \"{old}\" were said by {name}; "
                        + $"later lines use the name. Update the attendees and anything you attributed to \"{old}\".");
            // An attendee Claude matched to the old label is someone else now, unless it is the same person.
            foreach (var a in _state.Attendees.Where(a => a.Speaker == old && a.Id != name.ToLowerInvariant()).ToList())
                _state.Attendees[_state.Attendees.IndexOf(a)] = a with { Speaker = null };
        }
        else
        {
            _events.Add($"user: a speaker is {name}.");
        }
        var contact = _context.Contacts.FirstOrDefault(c => AssistText.SameName(c.Name, name));
        var changed = _state.Apply(new AssistOp.SetAttendee(new Attendee
        {
            Name = name, Speaker = name, ContactID = contact?.Id, Present = true,
        }));
        changed.Add("speakers");
        _state.Changed = changed.ToHashSet();
        SetStatus(_status);
    });

    // MARK: User actions (spec 02 §2.7)

    public Task SuggestNowAsync() => Do(() => { _events.Add(SuggestEvent); _forceNow = true; });

    /// <summary>While paused no updates are sent; the transcript keeps collecting and is sent on resume.</summary>
    public Task SetPausedAsync(bool p) => Do(() =>
    {
        _paused = p;
        if (!p) _forceNow = _pending.Count > 0;
        SetStatus(p ? new AssistStatus.Paused() : _busy ? new AssistStatus.Thinking() : new AssistStatus.Listening());
    });

    public Task MarkAskedAsync(string id) => Do(() =>
    {
        var i = _state.Questions.FindIndex(q => q.Id == id);
        if (i < 0) return;
        var q = _state.Questions[i] with { Status = QuestionStatus.Asked };
        _state.Questions.RemoveAt(i);
        _state.AskedQuestions.Add(q);
        _events.Add($"user marked question as asked: {q.Text}");
        _state.Changed = new();
        SetStatus(_status);
    });

    public Task DismissAsync(string id) => Do(() =>
    {
        var i = _state.Questions.FindIndex(q => q.Id == id);
        if (i < 0) return;
        var q = _state.Questions[i];
        _state.Questions.RemoveAt(i);
        _events.Add($"user dismissed question: {q.Text} — suggest something else");
        if (_state.Questions.Count == 0) _forceNow = true;
        _state.Changed = new();
        SetStatus(_status);
    });

    public Task PromoteAsync(string id) => Do(() =>
    {
        var i = _state.Questions.FindIndex(q => q.Id == id);
        if (i <= 0) return;
        var q = _state.Questions[i];
        _state.Questions.RemoveAt(i);
        _state.Questions.Insert(0, q);
        _state.Changed = new();
        SetStatus(_status);
    });

    public Task SetOpenPointAsync(string id, OpenPointStatus st) => Do(() =>
    {
        var p = _state.OpenPoints.FirstOrDefault(x => x.Id == id);
        if (p is null) return;
        p.Status = st;
        _events.Add($"user set open point {id} to {AssistPrompts.Raw(st)}");
        _state.Changed = new();
        SetStatus(_status);
    });

    /// <summary>The user filled or corrected a table cell; Claude keeps the user's value.</summary>
    public Task SetTableCellAsync(string row, string column, string value) => Do(() =>
    {
        var tid = row.Split('.')[0];
        var t = _state.Tables?.FirstOrDefault(x => x.Id == tid);
        if (t is null || !t.Set(row, column, value, byUser: true)) return;
        _events.Add($"user set table cell {row} «{column}» to «{value}»");
        _state.Changed = new();
        SetStatus(_status);
    });

    public Task SetPlaybookAsync(string id, PlaybookStatus st) => Do(() =>
    {
        var p = _state.Playbook.FirstOrDefault(x => x.Id == id);
        if (p is null) return;
        p.Status = st;
        _events.Add($"user set playbook item {id} to {AssistPrompts.Raw(st)}");
        _state.Changed = new();
        SetStatus(_status);
    });

    /// <summary>Every line collected so far (history + pending); used for catch-up and stored in assist.json.</summary>
    public Task<List<AssistLine>> GetTranscriptAsync() => Do(() => _history.Concat(_pending).ToList());

    /// <summary>A copy of the current state.</summary>
    public Task<AssistState> GetStateAsync() => Do(() => _state.Clone());

    /// <summary>Latency per answered update: time to the first op and in total (seconds).</summary>
    public Task<List<(double? FirstOp, double Total)>> GetLatenciesAsync() => Do(() => _latencies.ToList());

    /// <summary>Runs one tick now (tests; the loop calls it every 0.5 s).</summary>
    internal Task TickAsync() => Do(Tick);

    /// <summary>The events queued for the next update (tests).</summary>
    internal Task<List<string>> GetQueuedEventsAsync() => Do(() => _events.ToList());

    // MARK: Scheduling (spec 02 §2.4)

    void Tick()
    {
        if (_stopping || _stream is null || _paused) return;
        if (_limitedUntil is { } until)
        {
            if (Now < until) return;
            _limitedUntil = null;
            _forceNow = true;
        }
        if (_busy)
        {
            // A hung update: no answer for 90 s.
            if ((Now - _lastSend).TotalSeconds > HangSeconds) Restart("no answer");
            return;
        }
        if (_pendingRestart || NeedsFreshConversation) { _pendingRestart = false; Restart(null); return; }

        var since = _lastSend == DateTimeOffset.MinValue ? double.MaxValue : (Now - _lastSend).TotalSeconds;
        var words = _pending.Sum(l => AssistText.WordCount(l.Text));
        var questionFromThem = _pending.Count > 0 && _pending[^1].Speaker != MyName && _pending[^1].Text.EndsWith('?');
        var due = _forceNow
                  || (words >= _settings.MinWords && since >= _settings.MinInterval)
                  || (_pending.Count > 0 && since >= _settings.MaxInterval)
                  || (questionFromThem && since >= QuestionDelaySeconds)
                  || (_events.Count > 0 && _pending.Count == 0 && since >= _settings.MinInterval);
        if (!due || (_pending.Count == 0 && _events.Count == 0 && !_forceNow)) return;

        var lines = _pending;
        var evs = _events;
        _pending = new(); _events = new(); _forceNow = false;
        _history.AddRange(lines);
        try
        {
            Send(AssistPrompts.TickMessage(lines.Select(l => l.Rendered).ToList(),
                                           evs.Count == 0 && lines.Count == 0 ? new List<string> { SuggestEvent } : evs, _state.Summary));
            _unansweredEvents = evs;
        }
        catch (Exception)
        {
            _pending = lines.Concat(_pending).ToList();
            _events = evs.Concat(_events).ToList();
            _history.RemoveRange(_history.Count - lines.Count, lines.Count);
            Restart("write failed");
        }
    }

    bool NeedsFreshConversation =>
        _lastContextTokens > _settings.RestartTokens || (Now - _conversationStart).TotalMinutes > _settings.RestartMinutes;

    /// <summary>Starts a new conversation that carries the panels, the recent transcript and unanswered events over (spec 02 §2.5).</summary>
    void Restart(string? reason)
    {
        if (reason is not null) SetStatus(new AssistStatus.Reconnecting(reason));
        _stream?.Stop(); _stream = null;
        _busy = false;
        _restarts += 1;
        var cutoff = (_history.Count > 0 ? _history[^1].Start : 0) - ResumeWindowSeconds;
        var recent = _history.Where(l => l.Start >= cutoff).Select(l => l.Rendered).ToList();
        // Lines that were never answered go into the new conversation as recent transcript.
        var carry = _pending.Select(l => l.Rendered).ToList();
        _history.AddRange(_pending);
        _pending = new();
        // Events of an update that was never answered, and events still queued, go along too.
        var evs = _unansweredEvents.Concat(_events).ToList();
        _events = new();
        _unansweredEvents = evs;
        OpenConversation(AssistPrompts.ResumeMessage(_context, _state, recent.Concat(carry).ToList(), evs));
    }

    // MARK: Output (spec 02 §2.5–2.6)

    void Handle(ClaudeStreamEvent ev, int gen)
    {
        if (gen != _generation) return;     // events from an old conversation
        switch (ev)
        {
            case ClaudeStreamEvent.TextDelta t:
                _outputBuffer += t.Text;
                int nl;
                while ((nl = _outputBuffer.IndexOf('\n')) >= 0)
                {
                    var line = _outputBuffer[..nl];
                    _outputBuffer = _outputBuffer[(nl + 1)..];
                    ApplyLine(line);
                }
                break;
            case ClaudeStreamEvent.TurnDone d:
                {
                    var r = d.Result;
                    if (_outputBuffer.Length > 0) { ApplyLine(_outputBuffer); _outputBuffer = ""; }
                    _busy = false;
                    _latencies.Add((_firstOpAt is { } f ? (f - _lastSend).TotalSeconds : null, (Now - _lastSend).TotalSeconds));
                    _costThisConversation = r.TotalCostUsd;
                    _state.CostUSD = _costBase + _costThisConversation;
                    _lastContextTokens = r.ContextTokens;
                    if (r.IsError)
                    {
                        if (ClaudeCli.IsUsageLimit(r.Text))
                        {
                            _limitedUntil = Now.AddSeconds(LimitRetrySeconds);
                            // The events were not answered; send them again with the next update.
                            _events = _unansweredEvents.Concat(_events).ToList();
                            _unansweredEvents = new();
                            SetStatus(new AssistStatus.Limited("Recording continues; retrying in 5 minutes."));
                        }
                        else
                        {
                            SetStatus(new AssistStatus.Failed("Claude: " + (r.Text.Length > 200 ? r.Text[..200] : r.Text)));
                            _pendingRestart = true;
                        }
                        return;
                    }
                    _state.Updates += 1;
                    _unansweredEvents = new();
                    _restarts = 0;
                    SetStatus(_paused ? new AssistStatus.Paused() : new AssistStatus.Listening());
                    break;
                }
            case ClaudeStreamEvent.Exited x:
                {
                    if (_stopping) return;
                    _stream = null;
                    _busy = false;
                    var msg = x.Stderr.Trim();
                    var low = msg.ToLowerInvariant();
                    if (low.Contains("login") || low.Contains("not logged in"))
                    {
                        SetStatus(new AssistStatus.Failed(ClaudeException.NotLoggedIn().Message));
                        return;
                    }
                    if (_restarts >= MaxRestarts)
                    {
                        SetStatus(new AssistStatus.Failed($"Claude keeps stopping (exit {x.Status}). {(msg.Length > 200 ? msg[..200] : msg)}"));
                        return;
                    }
                    var code = x.Status;
                    _ = Task.Delay(ExitRestartDelay, _time).ContinueWith(_ => Post(() =>
                    {
                        if (!_stopping) Restart($"Claude stopped (exit {code})");
                    }));
                    break;
                }
        }
    }

    void ApplyLine(string line)
    {
        var op = AssistOp.Parse(line, _yesNo);
        if (op is null) return;
        _firstOpAt ??= Now;
        if (op is AssistOp.NextQuestions) _state.Changed = new();
        // Keep Claude's attendee matched to the Aya contact id only if it is one we know.
        if (op is AssistOp.SetAttendee sa && sa.Attendee.ContactID is { } cid && !_context.Contacts.Any(c => c.Id == cid))
            op = new AssistOp.SetAttendee(sa.Attendee with { ContactID = null });
        foreach (var id in _state.Apply(op)) _state.Changed.Add(id);
        SetStatus(_busy ? new AssistStatus.Thinking() : _status);
    }

    void SetStatus(AssistStatus s)
    {
        _status = s;
        _publish(_state.Clone(), s);
    }
}
