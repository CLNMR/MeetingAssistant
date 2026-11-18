using System.Text.Json.Nodes;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Claude;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Assist;

/// <summary>The three post-meeting Claude calls; replaced by fakes in tests.</summary>
public interface IRecapClaude
{
    Task<List<ProposedValue>> DraftRecapAsync(string binary, string model, AssistRecord record, string sessionID, AyaListSchema schema,
                                              AyaConfig config, AyaRecord? existing);
    Task<List<TemplateTable>> CompleteTablesAsync(string binary, string model, AssistRecord record, string sessionID, IReadOnlyList<TemplateTable> tables);
    Task<CRMProposal> ProposeCrmAsync(string binary, string model, AssistRecord record, string sessionID, AyaRecord client,
                                      IReadOnlyList<AyaRecord> contacts, AyaListSchema clientSchema, AyaListSchema? contactSchema);
}

/// <summary>The real calls through <see cref="RecapBuilder"/>.</summary>
public sealed class RecapClaude : IRecapClaude
{
    public Task<List<ProposedValue>> DraftRecapAsync(string binary, string model, AssistRecord record, string sessionID, AyaListSchema schema,
                                                     AyaConfig config, AyaRecord? existing) =>
        RecapBuilder.DraftRecapAsync(binary, model, record, sessionID, schema, config, existing);
    public Task<List<TemplateTable>> CompleteTablesAsync(string binary, string model, AssistRecord record, string sessionID,
                                                         IReadOnlyList<TemplateTable> tables) =>
        RecapBuilder.CompleteTablesAsync(binary, model, record, sessionID, tables);
    public Task<CRMProposal> ProposeCrmAsync(string binary, string model, AssistRecord record, string sessionID, AyaRecord client,
                                             IReadOnlyList<AyaRecord> contacts, AyaListSchema clientSchema, AyaListSchema? contactSchema) =>
        RecapBuilder.ProposeCrmAsync(binary, model, record, sessionID, client, contacts, clientSchema, contactSchema);
}

/// <summary>
/// The review after a meeting (spec 02 §7.1, 03 §8): the recap draft and its target meeting, the template
/// tables, the CRM proposal, speaker names, and draft persistence.
/// </summary>
public sealed class AssistReviewController : ObservableObject
{
    /// <summary>The recap draft is saved this long after the last change.</summary>
    public static readonly TimeSpan DraftSaveDelay = TimeSpan.FromMilliseconds(600);
    /// <summary>How often the final transcript is polled.</summary>
    public static readonly TimeSpan TranscriptPoll = TimeSpan.FromSeconds(1);

    readonly IAssistHost _host;
    readonly AssistConnection _aya;
    readonly IRecapClaude _claude;
    readonly TimeProvider _time;
    /// Per meeting: waiting for its final transcript, then drafting. Not cancelled when another meeting is
    /// opened or the review is finished, so every meeting gets its recap; cancelled when the meeting is deleted.
    readonly Dictionary<string, CancellationTokenSource> _waits = new();
    CancellationTokenSource? _draftSave;

    public AssistReviewController(IAssistHost host, AssistConnection aya, IRecapClaude? claude = null, TimeProvider? time = null)
    {
        _host = host;
        _aya = aya;
        _claude = claude ?? new RecapClaude();
        _time = time ?? TimeProvider.System;
    }

    // MARK: State

    AssistRecord? _record;
    string? _targetMeetingID;
    AyaRecord? _targetRecord;
    List<AyaRecord> _dayMeetings = new();
    List<ProposedValue> _recapFields = new();
    Transcript? _transcript;
    StepState _recapState = StepState.IdleState, _tablesState = StepState.IdleState, _crmState = StepState.IdleState;
    List<TemplateTable> _tables = new();
    CRMProposal? _crm;

    /// The session under review.
    public string? SessionId { get; private set; }

    /// <summary>Review steps that take seconds (Claude, Aya). Each belongs to the meeting it started for (spec 03 §8).</summary>
    public enum ReviewJob { Recap, Tables, Crm, Upload, ApplyCrm }
    readonly Dictionary<string, HashSet<ReviewJob>> _jobs = new();
    /// CRM proposals that finished while another meeting was open (not saved to disk).
    readonly Dictionary<string, CRMProposal> _pendingCrm = new();

    /// <summary>True while <paramref name="job"/> runs for meeting <paramref name="id"/>.</summary>
    public bool IsRunning(string id, ReviewJob job) { lock (_jobs) return _jobs.TryGetValue(id, out var j) && j.Contains(job); }
    /// Marks the job as running; false when it already runs for that meeting.
    bool Begin(string id, ReviewJob job)
    {
        lock (_jobs)
        {
            if (!_jobs.TryGetValue(id, out var j)) _jobs[id] = j = new();
            return j.Add(job);
        }
    }
    void End(string id, ReviewJob job)
    {
        lock (_jobs) { if (_jobs.TryGetValue(id, out var j) && j.Remove(job) && j.Count == 0) _jobs.Remove(id); }
    }
    /// <summary>Meeting <paramref name="id"/> is the one shown in the review.</summary>
    public bool IsOpen(string id) => SessionId == id;

    /// <summary>
    /// Changes a meeting's saved record: the open one in memory and on disk, any other one on disk only
    /// (read fresh, so steps finishing one after another do not overwrite each other).
    /// </summary>
    void UpdateRecord(string id, Action<AssistRecord> change)
    {
        if (IsOpen(id) && Record is { } open) { change(open); Save(open); return; }
        lock (this)
        {
            if (AssistRecord.Load(id) is not { } r) return;
            change(r);
            try { r.Save(id); } catch (IOException) { }
        }
    }
    public AssistRecord? Record { get => _record; private set => Set(ref _record, value); }
    /// Where the recap goes: an existing Aya meeting, or null for a new one.
    public string? TargetMeetingID { get => _targetMeetingID; private set => Set(ref _targetMeetingID, value); }
    public AyaRecord? TargetRecord { get => _targetRecord; private set { if (Set(ref _targetRecord, value)) Raise(nameof(TargetChoices)); } }
    /// Meetings in Aya on the recording's day.
    public List<AyaRecord> DayMeetings { get => _dayMeetings; private set { if (Set(ref _dayMeetings, value)) Raise(nameof(TargetChoices)); } }
    /// <summary>The editable recap draft. Setting it schedules the autosave.</summary>
    public List<ProposedValue> RecapFields
    {
        get => _recapFields;
        set
        {
            if (_recapFields.SequenceEqual(value)) return;
            _recapFields = value;
            Raise();
            SaveDraftSoon();
        }
    }
    /// The final transcript (null until it is ready).
    public Transcript? Transcript { get => _transcript; private set => Set(ref _transcript, value); }
    public StepState RecapState { get => _recapState; private set => Set(ref _recapState, value); }
    /// The template's tables in the review: live values, completed from the final transcript, edited.
    public List<TemplateTable> Tables { get => _tables; private set => Set(ref _tables, value); }
    public StepState TablesState { get => _tablesState; private set => Set(ref _tablesState, value); }
    public CRMProposal? Crm { get => _crm; private set => Set(ref _crm, value); }
    public StepState CrmState { get => _crmState; private set => Set(ref _crmState, value); }

    public AssistState State => Record?.State ?? new AssistState();
    public AssistContext? Context => Record?.Context;

    /// <summary>Edits one recap field in place (value, include); schedules the autosave.</summary>
    public void UpdateField(string key, Action<ProposedValue> change)
    {
        var list = _recapFields.Select(f => f.Copy()).ToList();
        var f = list.FirstOrDefault(x => x.Key == key);
        if (f is null) return;
        change(f);
        RecapFields = list;
    }

    // MARK: Open / finish

    /// <summary>
    /// Opens the review of a session (spec 03 §8): flushes the previous draft, loads the record, sets the
    /// draft, tables and target, loads the day's meetings and starts wait-then-draft. False when there is no record.
    /// </summary>
    public bool Open(string id)
    {
        FlushDraft();
        if (AssistRecord.Load(id) is not { } rec) return false;
        SessionId = id;
        Record = rec;
        Crm = null; CrmState = StepState.IdleState;
        _recapFields = rec.RecapDraft?.Select(f => f.Copy()).ToList() ?? new();
        Raise(nameof(RecapFields));
        Transcript = LoadTranscript(id);
        RecapState = rec.RecapRecordID is not null ? new StepState.Done("Saved in Aya.") : StepState.IdleState;
        DayMeetings = new();
        TargetRecord = null;
        Tables = (rec.Tables ?? rec.State.Tables ?? new()).ToList();
        TablesState = rec.Tables is not null ? new StepState.Done() : StepState.IdleState;
        TargetMeetingID = rec.RecapRecordID ?? rec.Context.MeetingID;
        if (IsRunning(id, ReviewJob.Recap)) RecapState = new StepState.Working("Claude is writing the recap…");
        if (IsRunning(id, ReviewJob.Upload)) RecapState = new StepState.Working("Saving in Aya…");
        if (IsRunning(id, ReviewJob.Tables)) TablesState = new StepState.Working("Claude is completing the tables…");
        if (IsRunning(id, ReviewJob.Crm)) CrmState = new StepState.Working("Claude is reading the CRM records and the transcript…");
        if (IsRunning(id, ReviewJob.ApplyCrm)) CrmState = new StepState.Working("Writing to Aya…");
        lock (_jobs)
        {
            if (_pendingCrm.Remove(id, out var pending))
            {
                Crm = pending;
                CrmState = pending.IsEmpty ? new StepState.Done("No changes proposed.") : StepState.IdleState;
            }
        }
        Raise(nameof(State)); Raise(nameof(Context));
        _host.ShowAssist();
        _ = LoadTargetsAsync(id);
        StartWait(id);
        return true;
    }

    /// <summary>Finish review: flush the draft and reset everything.</summary>
    public void Finish()
    {
        FlushDraft();
        Record = null; SessionId = null; Transcript = null; _recapFields = new(); Raise(nameof(RecapFields));
        Crm = null; Tables = new(); TablesState = StepState.IdleState;
        RecapState = StepState.IdleState; CrmState = StepState.IdleState;
        TargetMeetingID = null; TargetRecord = null; DayMeetings = new();
    }

    // MARK: Draft persistence (spec 02 §7.1 step 6)

    /// <summary>Saves the edited recap 600 ms after the last change (only into the review it was scheduled for).</summary>
    public void SaveDraftSoon()
    {
        if (SessionId is not { } id) return;
        _draftSave?.Cancel();
        var cts = _draftSave = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(DraftSaveDelay, _time, cts.Token); } catch (OperationCanceledException) { return; }
            FlushDraft(only: id);
        });
    }

    /// <summary>Writes a pending draft now (also on quit). An empty draft is saved as null.</summary>
    public void FlushDraft(string? only = null)
    {
        _draftSave?.Cancel(); _draftSave = null;
        lock (this)
        {
            if (SessionId is not { } id || (only is not null && only != id) || Record is not { } rec) return;
            var draft = _recapFields.Count == 0 ? null : _recapFields.Select(f => f.Copy()).ToList();
            var same = rec.RecapDraft is null ? draft is null : draft is not null && rec.RecapDraft.SequenceEqual(draft);
            if (same) return;
            rec.RecapDraft = draft;
            try { rec.Save(id); } catch (IOException) { }
        }
    }

    // MARK: Target meeting (spec 02 §7.5)

    async Task LoadTargetsAsync(string id)
    {
        if (_aya.Service is not { } svc) return;
        var day = _host.Session(id)?.StartedAt ?? Record?.Context.Date ?? DateTime.UtcNow;
        List<AyaRecord> meetings;
        try { meetings = await svc.MeetingsOnDayOfAsync(day); } catch (AyaException) { meetings = new(); }
        if (!IsOpen(id)) return;   // another meeting was opened meanwhile
        DayMeetings = meetings;
        await LoadTargetRecordAsync();
    }

    async Task LoadTargetRecordAsync()
    {
        if (TargetMeetingID is not { } id) { TargetRecord = null; return; }
        var r = DayMeetings.FirstOrDefault(m => m.Id == id);
        if (r is null && _aya.Service is { } svc)
        {
            try { r = await svc.RecordAsync(AyaSlot.Meetings, id); } catch (AyaException) { }
        }
        if (TargetMeetingID != id) return;
        TargetRecord = r;
        if (_recapFields.Count > 0) RecapFields = RecapBuilder.Target(_recapFields, r);
    }

    /// <summary>Chooses where the recap is saved: an existing meeting (re-applies its values to the draft) or null for a new one.</summary>
    public async Task SetTargetAsync(string? id)
    {
        TargetMeetingID = id;
        if (id is null) { TargetRecord = null; RecapFields = RecapBuilder.Target(_recapFields, null); return; }
        await LoadTargetRecordAsync();
    }

    /// <summary>Target choices: the current target record first, then the day's meetings, without duplicates.</summary>
    public List<AyaRecord> TargetChoices
    {
        get
        {
            var output = new List<AyaRecord>();
            foreach (var r in (TargetRecord is null ? Enumerable.Empty<AyaRecord>() : new[] { TargetRecord }).Concat(DayMeetings))
                if (!output.Any(o => o.Id == r.Id)) output.Add(r);
            return output;
        }
    }

    /// <summary>"HH:MM · Title" plus " (planned)" for the meeting picked at preparation (spec 03 §8.2).</summary>
    public string TargetChoiceText(AyaRecord r)
    {
        var d = r.Date(_aya.Config.MeetingDateField);
        var time = d is { } x ? x.ToLocalTime().ToString("HH:mm") + " · " : "";
        return time + r.Title + (r.Id == Context?.MeetingID ? " (planned)" : "");
    }

    // MARK: Wait, carry names, draft (spec 02 §7.1 steps 3–5)

    /// Starts waiting for meeting <paramref name="id"/>'s final transcript, unless that already runs.
    void StartWait(string id)
    {
        CancellationTokenSource cts;
        lock (_waits)
        {
            if (_waits.ContainsKey(id)) return;
            cts = _waits[id] = new CancellationTokenSource();
        }
        _ = RunWaitAsync(id, cts);
    }

    async Task RunWaitAsync(string id, CancellationTokenSource cts)
    {
        try { await WaitForTranscriptThenDraftAsync(id, cts.Token); }
        finally { lock (_waits) { if (_waits.TryGetValue(id, out var c) && c == cts) _waits.Remove(id); } }
    }

    /// <summary>True while meeting <paramref name="id"/> waits for its final transcript (or drafts after it).</summary>
    public bool IsWaiting(string id) { lock (_waits) return _waits.ContainsKey(id); }

    /// <summary>Stops waiting for a meeting's transcript (the meeting is deleted).</summary>
    public void CancelWait(string id)
    {
        CancellationTokenSource? cts;
        lock (_waits) { if (!_waits.Remove(id, out cts)) return; }
        cts.Cancel();
    }

    /// <summary>
    /// The recap uses the final transcript, so wait for transcription first (falls back to the live one). Runs per
    /// meeting in the background; when it is done the recap (and the tables) are drafted for that meeting, open or not.
    /// </summary>
    async Task WaitForTranscriptThenDraftAsync(string id, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var s = _host.Session(id)?.Status;
                if (s is null or SessionStatus.Done or SessionStatus.Failed or SessionStatus.Recorded) break;
                if (IsOpen(id))
                {
                    var p = _host.Progress(id);
                    RecapState = new StepState.Working("Waiting for the final transcript… " + (p is { } f ? $"{(int)(f * 100)} %" : ""));
                }
                await Task.Delay(TranscriptPoll, _time, ct);
            }
        }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested || _host.Session(id) is null) return;
        CarrySpeakerNames(id);
        var open = IsOpen(id);
        var rec = open ? Record : AssistRecord.Load(id);
        var draft = open ? _recapFields : rec?.RecapDraft ?? new();
        if (rec is null || draft.Count > 0 || IsRunning(id, ReviewJob.Recap))
        {
            if (IsOpen(id) && RecapState is StepState.Working w && w.Message.StartsWith("Waiting for the final transcript", StringComparison.Ordinal))
                RecapState = StepState.IdleState;
            return;
        }
        if (rec.RecapRecordID is null)
        {
            var tables = open ? Tables : rec.Tables ?? rec.State.Tables ?? new();
            var needTables = rec.Tables is null && tables.Count > 0 && !IsRunning(id, ReviewJob.Tables);
            await Task.WhenAll(DraftRecapAsync(id), needTables ? CompleteTablesAsync(id) : Task.CompletedTask);
        }
        else if (IsOpen(id)) RecapState = new StepState.Done("Already saved in Aya. Draft again to change it.");
    }

    /// <summary>
    /// Draft Recap / Draft Again (spec 03 §8.2), for the open meeting or, after wait-then-draft, for
    /// <paramref name="sessionId"/> even when it is not open (the draft is then saved to its record).
    /// </summary>
    public async Task DraftRecapAsync(string? sessionId = null)
    {
        if ((sessionId ?? SessionId) is not { } id || IsRunning(id, ReviewJob.Recap)) return;
        if ((IsOpen(id) ? Record : AssistRecord.Load(id)) is not { } rec) return;
        if (_aya.Schema(AyaSlot.Meetings) is not { } schema)
        {
            if (IsOpen(id)) RecapState = new StepState.Failed("Connect Aya in Settings › Assist first.");
            return;
        }
        if (await _aya.EnsureClaudeAsync() is not { } b)
        {
            if (IsOpen(id)) RecapState = new StepState.Failed(ClaudeException.NotInstalled().Message);
            return;
        }
        if (!Begin(id, ReviewJob.Recap)) return;
        try
        {
            if (IsOpen(id)) RecapState = new StepState.Working("Claude is writing the recap…");
            if (IsOpen(id) && TargetMeetingID is not null && TargetRecord is null) await LoadTargetRecordAsync();
            // The meeting it is saved to; when another meeting was opened meanwhile, it is matched again on opening.
            var existing = IsOpen(id) ? TargetRecord : null;
            var speakersBefore = LoadTranscript(id);
            var fields = await _claude.DraftRecapAsync(b, _aya.Settings.Model, rec, id, schema, _aya.Config, existing);
            // Speakers named while Claude was writing: the draft still has their old labels.
            if (speakersBefore is not null && LoadTranscript(id) is { } now)
                fields = SpeakerNames.Applying(SpeakerNames.Renames(speakersBefore, now), fields);
            if (IsOpen(id))
            {
                RecapFields = fields;
                RecapState = StepState.IdleState;
            }
            else UpdateRecord(id, r => r.RecapDraft = fields.Count == 0 ? null : fields);
            NameSession(id, RecapBuilder.Title(fields, _aya.Config), rec.Context.Title, force: false);
        }
        catch (Exception e) when (e is ClaudeException or AyaException or IOException)
        {
            if (IsOpen(id)) RecapState = new StepState.Failed(e.Message);
        }
        finally { End(id, ReviewJob.Recap); }
    }

    /// <summary>
    /// Complete from Transcript / Complete Again: Claude fills the tables (user cells stay); saved right away.
    /// For <paramref name="sessionId"/> when it is not open, the tables come from its saved record.
    /// </summary>
    public async Task CompleteTablesAsync(string? sessionId = null)
    {
        if ((sessionId ?? SessionId) is not { } id || IsRunning(id, ReviewJob.Tables)) return;
        if ((IsOpen(id) ? Record : AssistRecord.Load(id)) is not { } rec) return;
        var tables = IsOpen(id) ? Tables : rec.Tables ?? rec.State.Tables ?? new();
        if (tables.Count == 0) return;
        if (await _aya.EnsureClaudeAsync() is not { } b)
        {
            if (IsOpen(id)) TablesState = new StepState.Failed(ClaudeException.NotInstalled().Message);
            return;
        }
        if (!Begin(id, ReviewJob.Tables)) return;
        try
        {
            if (IsOpen(id)) TablesState = new StepState.Working("Claude is completing the tables…");
            var done = await _claude.CompleteTablesAsync(b, _aya.Settings.Model, rec, id, tables);
            UpdateRecord(id, r => r.Tables = done);
            if (IsOpen(id))
            {
                Tables = done;
                TablesState = new StepState.Done();
            }
        }
        catch (Exception e) when (e is ClaudeException or IOException)
        {
            if (IsOpen(id)) TablesState = new StepState.Failed(e.Message);
        }
        finally { End(id, ReviewJob.Tables); }
    }

    /// <summary>The user edits a table cell in the review (user-set, saved right away).</summary>
    public void SetCell(string row, string column, string value)
    {
        var tid = row.Split('.')[0];
        var t = Tables.FirstOrDefault(x => x.Id == tid);
        if (t is null || !t.Set(row, column, value, byUser: true)) return;
        Raise(nameof(Tables));
        if (SessionId is not { } id) return;
        var tables = Tables;
        UpdateRecord(id, r => r.Tables = tables);
    }

    /// Label of the meeting field the tables are saved to, null when they are not saved.
    public string? TablesFieldLabel =>
        _aya.Schema(AyaSlot.Meetings) is { } s && _aya.Config.TablesField(s) is { } key ? s.Field(key)?.Label ?? key : null;

    void Save(AssistRecord rec)
    {
        if (SessionId is not { } id) return;
        lock (this) { try { rec.Save(id); } catch (IOException) { } }
        Raise(nameof(Record));
    }

    // MARK: Upload (spec 02 §7.5)

    /// <summary>Create Meeting in Aya / Save to Meeting.</summary>
    public async Task UploadRecapAsync()
    {
        if (SessionId is not { } id || Record is not { } rec || _aya.Service is not { } svc || _aya.Config.List(AyaSlot.Meetings) is not { } r
            || _aya.Schema(AyaSlot.Meetings) is not { } schema) return;
        if (!Begin(id, ReviewJob.Upload)) return;
        // Everything the upload uses is taken now, so opening another meeting meanwhile changes nothing.
        var fields = _recapFields.Select(f => f.Copy()).ToList();
        var tables = Tables;
        var target = TargetMeetingID;
        var started = _host.Session(id)?.StartedAt ?? rec.Context.Date;
        var people = new[] { rec.Context.MyName }.Concat(rec.State.Attendees.Where(a => a.Present != false).Select(a => a.Name));
        var memberIDs = RecapBuilder.MemberIDs(people, _aya.Members);
        RecapState = new StepState.Working("Saving in Aya…");
        string? written = null;
        try
        {
            AyaRecord saved;
            if (target is not null)
            {
                var current = await svc.RecordAsync(AyaSlot.Meetings, target);
                var values = RecapBuilder.RecapValues(fields, rec, id, started, schema, _aya.Config, current, memberIDs, tables);
                written = Written(values);
                saved = await svc.Client.UpdateAsync(r.WorkspaceId, r.TechName, target, values);
            }
            else
            {
                var values = RecapBuilder.RecapValues(fields, rec, id, started, schema, _aya.Config, null, memberIDs, tables);
                written = Written(values);
                saved = await svc.Client.CreateAsync(r.WorkspaceId, r.TechName, values);
            }
            UpdateRecord(id, x =>
            {
                x.RecapRecordID = saved.Id;
                x.RecapUploadedAt = DateTime.UtcNow;
                x.RecapDraft = fields.Count == 0 ? null : fields;
            });
            _host.MarkSavedToAya(id);
            // The sidebar shows the title the meeting has in Aya.
            NameSession(id, written ?? saved.Title, rec.Context.Title, force: true);
            if (IsOpen(id))
            {
                TargetMeetingID = saved.Id;
                TargetRecord = saved;
                RecapState = new StepState.Done($"Saved in Aya: “{saved.Title}” ({r.Label}).");
            }
        }
        catch (Exception e) when (e is AyaException or IOException)
        {
            if (IsOpen(id)) RecapState = new StepState.Failed(e.Message);
        }
        finally { End(id, ReviewJob.Upload); }
    }

    /// The title value being written, when the configured title field is a string.
    string? Written(IReadOnlyDictionary<string, JsonNode?> values) =>
        values.TryGetValue(_aya.Config.MeetingTitleField, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Names a session after its meeting's title (spec 03 §8.2, Session name = Aya title). Without
    /// <paramref name="force"/> only a session that still has its automatic name ("Meeting" or the name it
    /// got at the start) is renamed, so a name the user typed stays.
    /// </summary>
    public void NameSession(string id, string? title, string automatic, bool force)
    {
        if (title is null) return;
        var t = RecapBuilder.SessionTitle(title);
        if (t.Length == 0 || _host.Session(id) is not { } s || s.Title == t) return;
        if (!force && s.Title != "Meeting" && s.Title != automatic) return;
        _host.RenameSession(id, t);
    }

    /// Meetings saved to Aya before sessions were named after their title get that name once (at app start).
    public void NameSavedMeetings()
    {
        foreach (var s in _host.Sessions.ToList())
        {
            if (!_host.IsSavedToAya(s.Id) || AssistRecord.Load(s.Id) is not { RecapRecordID: not null, RecapDraft: { } draft } rec) continue;
            NameSession(s.Id, RecapBuilder.Title(draft, _aya.Config), rec.Context.Title, force: false);
        }
    }

    /// The Aya web link of the uploaded meeting.
    public string? UploadedUrl => Record?.RecapRecordID is { } rid ? _aya.Config.WebUrl(AyaSlot.Meetings, rid) : null;

    // MARK: CRM (spec 02 §7.8)

    /// <summary>Propose Updates / Propose Again: fresh client and contact records, then Claude.</summary>
    public async Task ProposeCrmAsync()
    {
        if (SessionId is not { } id || Record is not { } rec || _aya.Service is not { } svc
            || _aya.Config.List(AyaSlot.Clients) is null || _aya.Schema(AyaSlot.Clients) is not { } cs) return;
        if (rec.Context.ClientID is not { } clientID) { CrmState = new StepState.Done("This meeting has no client."); return; }
        if (IsRunning(id, ReviewJob.Crm)) return;
        if (await _aya.EnsureClaudeAsync() is not { } b)
        {
            if (IsOpen(id)) CrmState = new StepState.Failed(ClaudeException.NotInstalled().Message);
            return;
        }
        if (!Begin(id, ReviewJob.Crm)) return;
        if (IsOpen(id)) CrmState = new StepState.Working("Claude is reading the CRM records and the transcript…");
        try
        {
            var client = await svc.RecordAsync(AyaSlot.Clients, clientID);
            var contacts = new List<AyaRecord>();
            if (_aya.Config.List(AyaSlot.Contacts) is not null)
                foreach (var c in rec.Context.Contacts)
                {
                    try { contacts.Add(await svc.RecordAsync(AyaSlot.Contacts, c.Id)); } catch (AyaException) { }
                }
            var proposal = await _claude.ProposeCrmAsync(b, _aya.Settings.Model, rec, id, client, contacts, cs, _aya.Schema(AyaSlot.Contacts));
            if (!IsOpen(id)) { lock (_jobs) _pendingCrm[id] = proposal; return; }   // shown when that meeting is opened again
            Crm = proposal;
            CrmState = proposal.IsEmpty ? new StepState.Done("No changes proposed.") : StepState.IdleState;
        }
        catch (Exception e) when (e is ClaudeException or AyaException or IOException)
        {
            if (IsOpen(id)) CrmState = new StepState.Failed(e.Message);
        }
        finally { End(id, ReviewJob.Crm); }
    }

    /// <summary>The count on "Apply Selected (N)": ticked client fields plus ticked contacts.</summary>
    public int SelectedCount => (Crm?.Client.Count(c => c.Include) ?? 0) + (Crm?.Contacts.Count(c => c.Include) ?? 0);

    /// <summary>Tells bindings the proposal's ticks or values changed.</summary>
    public void CrmEdited() { Raise(nameof(Crm)); Raise(nameof(SelectedCount)); }

    /// <summary>
    /// Writes the ticked items (spec 02 §7.8 "Applying"): client fields in one update, ticked existing contacts
    /// updated, new contacts created and linked. On a partial failure the message lists what was saved.
    /// </summary>
    public async Task ApplyCrmAsync()
    {
        if (SessionId is not { } id || Record is not { } rec || Crm is not { } p || _aya.Service is not { } svc
            || _aya.Config.List(AyaSlot.Clients) is not { } cRef || rec.Context.ClientID is not { } clientID) return;
        if (!Begin(id, ReviewJob.ApplyCrm)) return;
        CrmState = new StepState.Working("Writing to Aya…");
        var done = new List<string>();
        var createdIDs = new List<string>();
        try
        {
            if (_aya.Schema(AyaSlot.Clients) is { } cs)
            {
                var values = new Dictionary<string, JsonNode?>();
                foreach (var c in p.Client.Where(c => c.Include)) if (cs.TryCoerce(c.Value, c.Key, out var v)) values[c.Key] = v;
                if (values.Count > 0)
                {
                    await svc.Client.UpdateAsync(cRef.WorkspaceId, cRef.TechName, clientID, values);
                    done.Add($"{values.Count} client field{(values.Count == 1 ? "" : "s")}");
                }
            }
            if (_aya.Config.List(AyaSlot.Contacts) is { } pRef && _aya.Schema(AyaSlot.Contacts) is { } ps)
            {
                int updated = 0, created = 0;
                foreach (var c in p.Contacts.Where(c => c.Include))
                {
                    if (c.ContactID is { } cid)
                    {
                        var values = new Dictionary<string, JsonNode?>();
                        foreach (var ch in c.Changes.Where(ch => ch.Include)) if (ps.TryCoerce(ch.Value, ch.Key, out var v)) values[ch.Key] = v;
                        if (values.Count == 0) continue;
                        await svc.Client.UpdateAsync(pRef.WorkspaceId, pRef.TechName, cid, values);
                        updated += 1;
                    }
                    else
                    {
                        var values = RecapBuilder.NewContactValues(c, clientID, _aya.Config.ContactsClientField, ps);
                        if (values.Count == 0) continue;
                        var r = await svc.Client.CreateAsync(pRef.WorkspaceId, pRef.TechName, values);
                        createdIDs.Add(r.Id);
                        created += 1;
                    }
                }
                if (updated > 0) done.Add($"{updated} contact{(updated == 1 ? "" : "s")} updated");
                if (created > 0) done.Add($"{created} contact{(created == 1 ? "" : "s")} created");
            }
            UpdateRecord(id, x => { x.CreatedContactIDs.AddRange(createdIDs); x.CrmAppliedAt = DateTime.UtcNow; });
            if (!IsOpen(id)) return;
            Crm = null;
            CrmState = new StepState.Done(done.Count == 0 ? "Nothing selected." : "Saved in Aya: " + string.Join(", ", done) + ".");
        }
        catch (Exception e) when (e is AyaException or IOException)
        {
            if (createdIDs.Count > 0) UpdateRecord(id, x => x.CreatedContactIDs.AddRange(createdIDs));   // created contacts stay linked
            if (IsOpen(id)) CrmState = new StepState.Failed(e.Message + (done.Count == 0 ? "" : $" (already saved: {string.Join(", ", done)})"));
        }
        finally { End(id, ReviewJob.ApplyCrm); }
    }

    // MARK: Speakers (spec 02 §5.3, 03 §8.1)

    static Transcript? LoadTranscript(string id) =>
        JsonIO.TryRead<Transcript>(AppPaths.TranscriptFile(id), out var t) ? t : null;

    /// <summary>
    /// Gives the final transcript's speakers the names they had during the meeting, once per record. Works for any
    /// meeting (read from disk when it is not open); the recap follows through <see cref="SpeakersChanged"/>.
    /// </summary>
    public void CarrySpeakerNames(string id)
    {
        var t0 = LoadTranscript(id);
        if (IsOpen(id)) Transcript = t0;
        if ((IsOpen(id) ? Record : AssistRecord.Load(id)) is not { } rec || rec.SpeakersCarried == true) return;
        try
        {
            if (t0 is null) return;
            var t = t0.Clone();
            var names = SpeakerNames.CarryOver(t, rec.Lines, rec.State.Attendees);
            if (names.Count == 0) return;
            foreach (var s in t.Speakers) if (names.TryGetValue(s.Id, out var n)) s.Label = n;
            try { JsonIO.Write(t, AppPaths.TranscriptFile(id)); } catch (IOException) { }
            SpeakersChanged(id, t0, t);
        }
        finally { UpdateRecord(id, r => r.SpeakersCarried = true); }
    }

    /// <summary>
    /// Speakers of a meeting's final transcript were named or merged (review, carry-over or Scribe's transcript
    /// edits): the recap follows, in the open review or in the saved draft (spec 03 §8 "Speaker names in the recap").
    /// </summary>
    public void SpeakersChanged(string id, Transcript before, Transcript after)
    {
        var renames = SpeakerNames.Renames(before, after);
        if (IsOpen(id)) Transcript = after;
        if (renames.Count == 0) return;
        if (IsOpen(id)) RecapFields = SpeakerNames.Applying(renames, _recapFields);
        else UpdateRecord(id, r => { if (r.RecapDraft is { } d) r.RecapDraft = SpeakerNames.Applying(renames, d); });
    }

    /// <summary>
    /// Names a speaker of the final transcript: saved, taught to the speaker library (not me, with a voice),
    /// and a generic old label is replaced in the recap values.
    /// </summary>
    public void RenameFinalSpeaker(string speakerID, string name)
    {
        var n = AssistText.TrimAll(name);
        if (SessionId is not { } id || n.Length == 0 || LoadTranscript(id) is not { } t) return;
        var sp = t.Speakers.FirstOrDefault(s => s.Id == speakerID);
        if (sp is null || sp.Label == n) return;
        var before = t.Clone();
        sp.Label = n;
        try { JsonIO.Write(t, AppPaths.TranscriptFile(id)); } catch (IOException) { }
        if (sp.Embedding is { } e && !sp.IsMe) _host.LearnSpeaker(n, e);
        SpeakersChanged(id, before, t);
    }

    public PersonCard? PersonCard(string name, string? contactID = null) =>
        AssistPeople.Card(name, contactID, Context, _aya.Members, _aya.Schema(AyaSlot.Members));

    public List<string> NameSuggestions => AssistPeople.NameSuggestions(State, Context, _aya.Members, _host.MyName);

    /// <summary>"Template: X of N points covered · missing: a; b; c…" (up to 5 missing), or null without a checklist (spec 03 §8.4).</summary>
    public string? ChecklistLine
    {
        get
        {
            var pb = State.Playbook;
            if (pb.Count == 0) return null;
            var covered = pb.Count(p => p.Status == PlaybookStatus.Covered);
            var missing = pb.Where(p => p.Status == PlaybookStatus.Missing).Select(p => p.Text).ToList();
            var s = $"Template: {covered} of {pb.Count} points covered";
            if (missing.Count > 0) s += " · missing: " + string.Join("; ", missing.Take(5));
            return s;
        }
    }
}
