using MeetingAssistant.Core.Claude;
using MeetingAssistant.Core.Input;
using MeetingAssistant.Core.Platform;
using MeetingAssistant.Core.Storage;
using MeetingAssistant.Core.Text;

namespace MeetingAssistant.Core.Dictation;

/// <summary>
/// Push-to-talk dictation, platform-neutral (spec 04 §1–§3): hold a shortcut, speak, release; the
/// text is transcribed locally, cleaned up (vocabulary, spoken forms), optionally rewritten by
/// Claude (Scribe+), fitted to the text around the cursor and pasted (or copied).
///
/// The platform parts come in through interfaces (microphone, focused field, paste, muter,
/// recognizer, clock), so the whole state machine is unit-tested with fakes.
///
/// Threading: call every member from one thread or synchronization context (the UI thread in the
/// app). Awaits resume there; only the microphone writes from its own thread (into a locked buffer).
/// <see cref="Changed"/> is raised on that context whenever a visible property changes.
/// </summary>
public sealed class DictationController : IDisposable
{
    // ---- Wiring ----
    readonly IDictationMicrophone _microphone;
    readonly IDictationRecognizer _recognizer;
    readonly IFocusedFieldSource _fields;
    readonly IDictationOutput _output;
    readonly IOutputMuter? _muter;
    readonly IDictationClock _clock;
    readonly DictationRecoveryStore _recovery;
    readonly ClaudeSpare _spare;
    readonly Func<Preferences> _prefs;
    readonly Func<bool> _isRecording;

    /// <param name="startSession">Starts a Scribe+ Claude process for a model (spec 04 §2.3); throws
    /// (e.g. <see cref="ClaudeException.NotInstalled"/>) when there is no `claude` binary.</param>
    /// <param name="prefs">Current preferences (read at each step, so settings apply at once).</param>
    /// <param name="isRecording">A meeting recording runs (then output is not muted, spec 04 §1.6).</param>
    public DictationController(
        IDictationMicrophone microphone,
        IDictationRecognizer recognizer,
        IFocusedFieldSource fields,
        IDictationOutput output,
        Func<string, ClaudeRewriteSession> startSession,
        Func<Preferences> prefs,
        IOutputMuter? muter = null,
        IDictationClock? clock = null,
        DictationRecoveryStore? recovery = null,
        Func<bool>? isRecording = null)
    {
        _microphone = microphone;
        _recognizer = recognizer;
        _fields = fields;
        _output = output;
        _muter = muter;
        _clock = clock ?? SystemDictationClock.Instance;
        _recovery = recovery ?? new DictationRecoveryStore();
        _prefs = prefs;
        _isRecording = isRecording ?? (() => false);
        _spare = new ClaudeSpare(startSession, () => _clock.UtcNow);
        RefreshUnfinished();
    }

    // ---- Observable state (spec 04 §1.1, §1.7, §1.8) ----

    /// Raised after any visible property changed.
    public event Action? Changed;
    /// One diagnostic line (spec 04 §1.9); text content is never logged.
    public event Action<string>? Log;

    public DictationPhase Phase { get; private set; }
    public DictationMode Mode { get; private set; }
    /// What happens while transcribing (`Transcribing…`, `Writing with Claude…`, the model hint, …).
    public string Detail { get; private set; } = DictationText.Transcribing;
    /// The toast text while <see cref="Phase"/> is Message.
    public string Message { get; private set; } = "";
    /// Mic RMS of the last 50 ms while listening.
    public float Level { get; private set; }
    /// Live preview while listening; the final text while pasting.
    public string Preview { get; private set; } = "";
    /// The last delivered text (tray "Copy Last Dictation"); in memory only.
    public string? LastText { get; private set; }
    /// Kept dictations, newest first (tray "Unfinished Dictations").
    public IReadOnlyList<DictationRecovery.Item> Unfinished { get; private set; } = Array.Empty<DictationRecovery.Item>();
    /// Timings of the last finished dictation.
    public DictationText.Steps LastSteps { get; private set; } = new();

    // ---- Vocabulary ----

    Vocabulary _vocabulary = new();
    VocabularyMatcher? _matcher;

    /// The vocabulary used for clean-up and Scribe+ terms; set it whenever it changes.
    public Vocabulary Vocabulary
    {
        get => _vocabulary;
        set { _vocabulary = value; _matcher = null; }
    }

    VocabularyMatcher Matcher => _matcher ??= _vocabulary.Matcher();

    // ---- Per-dictation state ----
    IAudioSource? _mic;
    Action<float[]>? _micHandler;
    SampleBuffer _buffer = new();
    CancellationTokenSource? _loops;
    CancellationTokenSource? _hide;
    CancellationTokenSource? _spareTimer;
    DateTime _startedAt;
    Task<DictationField?>? _startField;
    ClaudeRewriteSession? _plus;
    bool _previewRunning;
    int _generation;
    bool _disposed;

    void Notify() => Changed?.Invoke();

    // ================= Press / release / cancel (spec 04 §1.1–1.4) =================

    /// The shortcut for <paramref name="mode"/> went down (spec 04 §1.2). While listening, the Scribe+
    /// shortcut upgrades a plain dictation; every other press while busy is ignored.
    public void Press(DictationMode mode)
    {
        if (_disposed) return;
        switch (Phase)
        {
            case DictationPhase.Listening:
                if (mode == DictationMode.Plus && Mode == DictationMode.Plain)
                {
                    Mode = DictationMode.Plus;
                    StartPlusSession();
                    Notify();
                }
                return;
            case DictationPhase.Transcribing:
                return;
        }
        CancelHide();
        switch (_microphone.Permission)
        {
            case MicrophonePermission.NotDetermined:
                _microphone.RequestPermission();
                Flash(DictationText.AllowMicrophone);
                return;
            case MicrophonePermission.Denied:
                Flash(DictationText.NoMicrophoneAccess);
                return;
        }
        var buffer = new SampleBuffer();
        IAudioSource mic;
        Action<float[]> handler = buffer.Append;
        try
        {
            mic = _microphone.Create();
            mic.Samples += handler;
            try { mic.Start(); }
            catch { mic.Samples -= handler; mic.Dispose(); throw; }
        }
        catch (UnauthorizedAccessException)
        {
            Flash(DictationText.NoMicrophoneAccess);
            return;
        }
        catch (Exception e)
        {
            Log?.Invoke($"microphone failed: {e.Message}");
            Flash(DictationText.MicrophoneUnavailable);
            return;
        }
        _generation++;
        _mic = mic;
        _micHandler = handler;
        _buffer = buffer;
        Mode = mode;
        _startedAt = _clock.UtcNow;
        Phase = DictationPhase.Listening;
        Level = 0;
        Preview = "";
        Notify();
        if (mode == DictationMode.Plus) StartPlusSession();
        if (_prefs().MuteWhileDictating && !_isRecording()) Safe(() => _muter?.Mute());
        // After the microphone runs: reading the field can take a moment in some apps.
        _startField = _fields.ReadAsync(readText: false);
        _loops = new CancellationTokenSource();
        _ = LevelLoop(_loops.Token);
        _ = PreviewLoop(_generation, _loops.Token);
    }

    /// The shortcut was released (spec 04 §1.4). Completes when the text was delivered or the
    /// dictation ended otherwise.
    public async Task ReleaseAsync()
    {
        if (Phase != DictationPhase.Listening) return;
        var held = (_clock.UtcNow - _startedAt).TotalSeconds;
        Phase = DictationPhase.Transcribing;
        Detail = DictationText.Transcribing;
        Notify();
        try
        {
            // Let the last audio buffer arrive so the final word is not cut off.
            await _clock.Delay(TimeSpan.FromMilliseconds(DictationText.ReleaseDelayMs));
            var samples = StopMic();
            RestoreOutput();
            if (DictationText.IsTap(held)) { ClosePlus(); Hide(); return; }
            var voiced = InventedText.LongestVoicedRun(samples);
            // A shortcut pressed by accident: nothing that sounds like a word.
            if (DictationText.IsAccidental(held, voiced)) { ClosePlus(); Flash(DictationText.NoSpeech); return; }
            await FinishAsync(samples, voiced, Mode);
        }
        catch (Exception e)
        {
            Log?.Invoke($"dictation failed: {e.Message}");
            ClosePlus();
            Flash(DictationText.TranscriptionFailed);
        }
    }

    /// Another key was typed while a modifier-only shortcut was held: drop the recording silently.
    public void Cancel()
    {
        if (Phase != DictationPhase.Listening) return;
        StopMic();
        RestoreOutput();
        ClosePlus();
        Hide();
    }

    // ================= Processing (spec 04 §1.4 step 6) =================

    /// Everything after the recording: save for recovery, transcribe (while reading the focused
    /// field), clean up, Scribe+, fit, deliver. With <paramref name="deliverTo"/> nothing is saved
    /// or pasted; the final text and the Scribe+ note go there instead (test hook, msctl).
    public async Task<DictationText.Steps> FinishAsync(float[] samples, double voiced, DictationMode mode,
                                                      Func<string, string?, Task>? deliverTo = null)
    {
        var st = new DictationText.Steps { Mode = mode == DictationMode.Plus ? "plus" : "plain", Audio = samples.Length / 16_000.0 };
        var t0 = _clock.UtcNow;
        var t = t0;
        double Lap(ref DateTime since) { var now = _clock.UtcNow; var d = (now - since).TotalSeconds; since = now; return d; }
        try
        {
            var saved = deliverTo is null ? _recovery.Save(samples, mode == DictationMode.Plus, _clock.UtcNow) : null;
            using var hint = new CancellationTokenSource();
            _ = ModelHint(hint.Token);
            // The speech model runs off this thread; the field is read meanwhile.
            var asr = _recognizer.DictateAsync(samples);
            var p = _prefs();
            var tf = _clock.UtcNow;
            var field = await ReadField(p.FitToCursor || p.ScribePlusContext);
            st.Field = Lap(ref tf);
            string raw;
            try
            {
                raw = await asr;
            }
            catch (Exception e)
            {
                hint.Cancel();
                ClosePlus();
                RefreshUnfinished();
                st.Outcome = $"asr failed: {e.Message}";
                Flash(saved is null ? DictationText.TranscriptionFailed : DictationText.TranscriptionFailedKept);
                return st;
            }
            hint.Cancel();
            st.Asr = Lap(ref t);
            st.Raw = raw;
            var cleaned = DictationText.Clean(raw, voiced, Matcher);
            if (cleaned is null)
            {
                if (saved is not null) _recovery.Delete(saved);
                st.Outcome = "no speech";
                ClosePlus();
                Flash(DictationText.NoSpeech);
                return st;
            }
            var text = cleaned;
            st.Cleanup = Lap(ref t);
            string? note = null;
            if (mode == DictationMode.Plus)
            {
                (text, note) = await RewriteAsync(text, field, p.ScribePlusContext);
                st.Claude = Lap(ref t);
            }
            if (p.FitToCursor && field?.Before is { } before)
                text = InsertionFit.Fit(text, before, field.After ?? "");
            st.Text = text;
            st.Outcome = note ?? "ok";
            if (deliverTo is not null) { await deliverTo(text, note); return st; }
            var (delivered, hideLater) = await DeliverAsync(text, field, note);
            if (saved is not null)
            {
                if (delivered) _recovery.Delete(saved); else RefreshUnfinished();
            }
            if (hideLater) _ = HideAfterPaste(_generation);
            return st;
        }
        finally
        {
            st.Total = (_clock.UtcNow - t0).TotalSeconds;
            LastSteps = st;
            Log?.Invoke(st.LogLine());
        }
    }

    async Task<DictationField?> ReadField(bool readText)
    {
        try { return await _fields.ReadAsync(readText); }
        catch { return null; }
    }

    /// After 5 s still "Transcribing…": the model is being prepared (first dictation after an update).
    async Task ModelHint(CancellationToken ct)
    {
        try { await _clock.Delay(TimeSpan.FromSeconds(DictationText.ModelHintSeconds), ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested || Phase != DictationPhase.Transcribing || Detail != DictationText.Transcribing) return;
        Detail = DictationText.ModelHint;
        Notify();
    }

    // ================= Delivery (spec 04 §3.1) =================

    /// Pastes the text, or only copies it when the cursor moved to another field. Returns whether
    /// the text reached the clipboard, and whether the HUD should hide 0.6 s later (pasted, no note).
    async Task<(bool Delivered, bool HideLater)> DeliverAsync(string text, DictationField? field, string? note)
    {
        LastText = text;
        Notify();
        // Clicked into another field meanwhile: the text must not land there.
        var start = _startField is null ? null : await SafeField(_startField);
        if (start is not null)
        {
            var now = field ?? await ReadField(readText: false);
            if (now is not null && !start.Identity.IsSameField(now.Identity))
            {
                bool ok = _output.Copy(text);
                Flash(DictationText.CursorMoved);
                return (ok, false);
            }
        }
        Preview = text;
        Notify();
        PasteResult r;
        try { r = await _output.PasteAsync(text); }
        catch (Exception e) { Log?.Invoke($"paste failed: {e.Message}"); r = new PasteResult(false, "Could not paste – try again", Delivered: false); }
        if (r.Toast is not null) { Flash(r.Toast); return (r.Delivered, false); }
        if (note is not null) { Flash(note); return (true, false); }
        return (true, true);
    }

    /// The HUD shows the pasted text for 0.6 s, then hides (not counted in the step timings).
    async Task HideAfterPaste(int generation)
    {
        await _clock.Delay(TimeSpan.FromSeconds(DictationText.HideAfterPasteSeconds));
        if (Phase == DictationPhase.Transcribing && generation == _generation) Hide();
    }

    static async Task<DictationField?> SafeField(Task<DictationField?> t)
    {
        try { return await t; } catch { return null; }
    }

    // ================= Scribe+ (spec 04 §2) =================

    void StartPlusSession()
    {
        if (_plus is not null) return;
        try { _plus = _spare.Take(_prefs().ScribePlusModel); }
        catch (Exception e) { _plus = null; Log?.Invoke($"Scribe+ failed: {e.Message}"); }
    }

    void ClosePlus()
    {
        _plus?.Close();
        _plus = null;
        UpdateSpare(TimeSpan.FromSeconds(1));
    }

    /// The Claude rewrite; on any problem the word-for-word text with the note why (spec 04 §2.6).
    async Task<(string Text, string? Note)> RewriteAsync(string text, DictationField? field, bool withContext)
    {
        try
        {
            if (_plus is null) StartPlusSession();
            var session = _plus;
            if (session is null) return (text, ScribePlus.Note(ScribePlus.Failure.NoClaude));
            Detail = DictationText.WritingWithClaude;
            Notify();
            var v = _vocabulary;
            var terms = ScribePlus.RelevantTerms(text, v.Terms, v.UseAya ? v.AyaTerms : Array.Empty<string>());
            var prompt = ScribePlus.Prompt(text, field?.App, field?.Before, field?.After, withContext, terms);
            string answer;
            try
            {
                answer = await WithTimeout(session, prompt);
            }
            catch (ClaudeException e)
            {
                Log?.Invoke($"Scribe+ failed: {e.Message}");
                return e.Kind switch
                {
                    ClaudeErrorKind.UsageLimit => (text, ScribePlus.Note(ScribePlus.Failure.UsageLimit)),
                    ClaudeErrorKind.NotLoggedIn => (text, ScribePlus.Note(ScribePlus.Failure.NotLoggedIn)),
                    ClaudeErrorKind.NotInstalled => (text, ScribePlus.Note(ScribePlus.Failure.NoClaude)),
                    _ => (text, ScribePlus.Note(ScribePlus.Failure.Other)),
                };
            }
            catch (Exception e)
            {
                Log?.Invoke($"Scribe+ failed: {e.Message}");
                return (text, ScribePlus.Note(ScribePlus.Failure.Other));
            }
            var c = ScribePlus.Check(answer, text, withContext ? field?.Before : null);
            if (c.Problem is { } problem)
            {
                Log?.Invoke($"Scribe+ answer rejected: {problem.ToString().ToLowerInvariant()}");
                return (text, ScribePlus.Note(ScribePlus.Failure.Rejected));
            }
            return (c.Text, null);
        }
        finally
        {
            ClosePlus();
        }
    }

    /// Sends the prompt; 8 s on the controller's clock, counted from sending (spec 04 §2.3).
    async Task<string> WithTimeout(ClaudeRewriteSession session, string prompt)
    {
        // The session has its own (real-time) timeout as a backstop; ours decides.
        var rewrite = session.RewriteAsync(prompt, ScribePlus.TimeoutSeconds + 2);
        using var cts = new CancellationTokenSource();
        var timeout = _clock.Delay(TimeSpan.FromSeconds(ScribePlus.TimeoutSeconds), cts.Token);
        var done = await Task.WhenAny(rewrite, timeout);
        if (done != rewrite)
        {
            session.Close();
            _ = rewrite.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw ClaudeException.TimedOut((int)ScribePlus.TimeoutSeconds);
        }
        cts.Cancel();
        return await rewrite;
    }

    /// Starts, keeps or stops the spare Claude process to match the settings (spec 04 §2.3):
    /// call 3 s after app start or a settings change (the default); runs 1 s after each Scribe+
    /// dictation and then every 10 minutes on its own.
    public void UpdateSpare(TimeSpan? after = null)
    {
        if (_disposed) return;
        _spareTimer?.Cancel();
        _spareTimer = null;
        var p = _prefs();
        if (!SpareWanted(p)) { _spare.Refresh(false, p.ScribePlusModel); return; }
        var cts = new CancellationTokenSource();
        _spareTimer = cts;
        _ = SpareLater(after ?? TimeSpan.FromSeconds(3), cts.Token);
    }

    static bool SpareWanted(Preferences p) => p.DictationEnabled && p.ScribePlusHotKey is not null && p.ScribePlusWarm;

    async Task SpareLater(TimeSpan delay, CancellationToken ct)
    {
        try { await _clock.Delay(delay, ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested || _disposed) return;
        var p = _prefs();
        _spare.Refresh(SpareWanted(p), p.ScribePlusModel);
        // Check again later (replaces an old or crashed one).
        if (!ct.IsCancellationRequested) UpdateSpare(ClaudeSpare.CheckInterval);
    }

    // ================= Live preview and level (spec 04 §1.2 step 8, §1.3) =================

    async Task LevelLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _clock.Delay(TimeSpan.FromMilliseconds(DictationText.LevelIntervalMs), ct);
                if (ct.IsCancellationRequested) return;
                Level = DictationText.Rms(_buffer.Tail(DictationText.LevelSamples));
                Notify();
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task PreviewLoop(int generation, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _clock.Delay(TimeSpan.FromSeconds(DictationText.PreviewInterval), ct);
                if (ct.IsCancellationRequested) return;
                _ = RefreshPreview(generation);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// Transcribes the last ≤ 14 s (one Parakeet window). The final text after release always
    /// covers the whole recording; the preview never affects it.
    async Task RefreshPreview(int generation)
    {
        if (Phase != DictationPhase.Listening || _previewRunning || generation != _generation) return;
        var buffer = _buffer;
        int count = buffer.Count;
        if (count < DictationText.PreviewMinSamples) return;
        bool truncated = count > DictationText.PreviewWindowSamples;
        var clip = buffer.Tail(DictationText.PreviewWindowSamples);
        _previewRunning = true;
        string text;
        try { text = await _recognizer.DictateAsync(clip); }
        catch { text = ""; }
        finally { _previewRunning = false; }
        if (Phase == DictationPhase.Listening && generation == _generation && text.Length > 0)
        {
            Preview = DictationText.PreviewText(text, truncated);
            Notify();
        }
    }

    float[] StopMic()
    {
        _loops?.Cancel();
        _loops = null;
        var mic = _mic;
        _mic = null;
        if (mic is not null)
        {
            Safe(mic.Stop);
            if (_micHandler is not null) mic.Samples -= _micHandler;
            Safe(mic.Dispose);
        }
        _micHandler = null;
        Level = 0;
        return _buffer.All();
    }

    void RestoreOutput() => Safe(() => _muter?.Restore());

    // ================= Unfinished dictations (spec 04 §1.8) =================

    /// Re-reads the kept dictations (those older than 24 h are deleted on the way).
    public void RefreshUnfinished()
    {
        try { Unfinished = _recovery.Current(_clock.UtcNow); }
        catch { Unfinished = Array.Empty<DictationRecovery.Item>(); }
        Notify();
    }

    /// "Transcribe Again and Copy": the same text processing as a dictation (Scribe+ again for a
    /// `-plus` file, without app name or cursor context), never fitted, only copied. Ignored while
    /// a dictation is listening or transcribing.
    public async Task RetryAsync(DictationRecovery.Item item)
    {
        if (Phase is DictationPhase.Listening or DictationPhase.Transcribing) return;
        CancelHide();
        _generation++;
        Mode = item.Plus ? DictationMode.Plus : DictationMode.Plain;
        Phase = DictationPhase.Transcribing;
        Detail = DictationText.TranscribingKept;
        Preview = "";
        Notify();
        try
        {
            var samples = _recovery.Load(item);
            var raw = await _recognizer.DictateAsync(samples);
            var cleaned = DictationText.Clean(raw, InventedText.LongestVoicedRun(samples), Matcher);
            if (cleaned is null)
            {
                _recovery.Delete(item.Path);
                RefreshUnfinished();
                Flash(DictationText.NoSpeechInRecording);
                return;
            }
            var text = cleaned;
            string? note = null;
            if (item.Plus) (text, note) = await RewriteAsync(text, null, withContext: false);
            _output.Copy(text);
            LastText = text;
            _recovery.Delete(item.Path);
            RefreshUnfinished();
            // The notes are worded for a paste; a retry only copies.
            Flash(note is null ? DictationText.CopiedNotPasted : ScribePlus.CopiedNote(note));
        }
        catch (Exception e)
        {
            Log?.Invoke($"retry failed: {e.Message}");
            ClosePlus();
            Flash(DictationText.TranscriptionFailedAgain);
        }
    }

    /// Deletes a kept dictation.
    public void Discard(DictationRecovery.Item item)
    {
        _recovery.Delete(item.Path);
        RefreshUnfinished();
    }

    /// Copies <see cref="LastText"/> again (tray "Copy Last Dictation").
    public bool CopyLast() => LastText is { } t && _output.Copy(t);

    // ================= HUD =================

    void Flash(string text)
    {
        Phase = DictationPhase.Message;
        Message = text;
        Notify();
        CancelHide();
        var cts = new CancellationTokenSource();
        _hide = cts;
        _ = HideLater(TimeSpan.FromSeconds(DictationText.ToastDuration(text)), cts.Token);
    }

    async Task HideLater(TimeSpan delay, CancellationToken ct)
    {
        try { await _clock.Delay(delay, ct); }
        catch (OperationCanceledException) { return; }
        if (!ct.IsCancellationRequested) Hide();
    }

    void CancelHide()
    {
        _hide?.Cancel();
        _hide = null;
    }

    void Hide()
    {
        Phase = DictationPhase.Idle;
        Preview = "";
        Notify();
    }

    static void Safe(Action a)
    {
        try { a(); } catch { /* platform hiccups must not break a dictation */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Phase == DictationPhase.Listening) { StopMic(); RestoreOutput(); }
        _disposed = true;
        _spareTimer?.Cancel();
        CancelHide();
        _plus?.Close();
        _plus = null;
        _spare.Dispose();
    }
}
