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
    CancellationTokenSource? _wait;
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
        _wait?.Cancel();
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
        Raise(nameof(State)); Raise(nameof(Context));
        _host.ShowAssist();
        _ = LoadTargetsAsync(id);
        var cts = _wait = new CancellationTokenSource();
        _ = WaitForTranscriptThenDraftAsync(id, cts.Token);
        return true;
    }

    /// <summary>Finish review: flush the draft and reset everything.</summary>
    public void Finish()
    {
        _wait?.Cancel();
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
        try { DayMeetings = await svc.MeetingsOnDayOfAsync(day); } catch (AyaException) { DayMeetings = new(); }
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

    async Task WaitForTranscriptThenDraftAsync(string id, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var s = _host.Session(id)?.Status;
                if (s is null or SessionStatus.Done or SessionStatus.Failed or SessionStatus.Recorded) break;
                var p = _host.Progress(id);
                RecapState = new StepState.Working("Waiting for the final transcript… " + (p is { } f ? $"{(int)(f * 100)} %" : ""));
                await Task.Delay(TranscriptPoll, _time, ct);
            }
        }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;
        CarrySpeakerNames(id);
        if (_recapFields.Count > 0) return;
        if (Record?.RecapRecordID is null)
        {
            var needTables = Record?.Tables is null && Tables.Count > 0;
            await Task.WhenAll(DraftRecapAsync(), needTables ? CompleteTablesAsync() : Task.CompletedTask);
        }
        else RecapState = new StepState.Done("Already saved in Aya. Draft again to change it.");
    }

    /// <summary>Draft Recap / Draft Again (spec 03 §8.2).</summary>
    public async Task DraftRecapAsync()
    {
        if (SessionId is not { } id || Record is not { } rec) return;
        if (_aya.Schema(AyaSlot.Meetings) is not { } schema) { RecapState = new StepState.Failed("Connect Aya in Settings › Assist first."); return; }
        if (await _aya.EnsureClaudeAsync() is not { } b) { RecapState = new StepState.Failed(ClaudeException.NotInstalled().Message); return; }
        RecapState = new StepState.Working("Claude is writing the recap…");
        try
        {
            if (TargetMeetingID is not null && TargetRecord is null) await LoadTargetRecordAsync();
            RecapFields = await _claude.DraftRecapAsync(b, _aya.Settings.Model, rec, id, schema, _aya.Config, TargetRecord);
            RecapState = StepState.IdleState;
        }
        catch (Exception e) when (e is ClaudeException or AyaException or IOException) { RecapState = new StepState.Failed(e.Message); }
    }

    /// <summary>Complete from Transcript / Complete Again: Claude fills the tables (user cells stay); saved right away.</summary>
    public async Task CompleteTablesAsync()
    {
        if (SessionId is not { } id || Record is not { } rec || Tables.Count == 0) return;
        if (await _aya.EnsureClaudeAsync() is not { } b) { TablesState = new StepState.Failed(ClaudeException.NotInstalled().Message); return; }
        TablesState = new StepState.Working("Claude is completing the tables…");
        try
        {
            var done = await _claude.CompleteTablesAsync(b, _aya.Settings.Model, rec, id, Tables);
            Tables = done;
            rec.Tables = done;
            Save(rec);
            TablesState = new StepState.Done();
        }
        catch (Exception e) when (e is ClaudeException or IOException) { TablesState = new StepState.Failed(e.Message); }
    }

    /// <summary>The user edits a table cell in the review (user-set, saved right away).</summary>
    public void SetCell(string row, string column, string value)
    {
        var tid = row.Split('.')[0];
        var t = Tables.FirstOrDefault(x => x.Id == tid);
        if (t is null || !t.Set(row, column, value, byUser: true)) return;
        Raise(nameof(Tables));
        if (Record is not { } rec) return;
        rec.Tables = Tables;
        Save(rec);
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
        var started = _host.Session(id)?.StartedAt ?? rec.Context.Date;
        var people = new[] { rec.Context.MyName }.Concat(rec.State.Attendees.Where(a => a.Present != false).Select(a => a.Name));
        var memberIDs = RecapBuilder.MemberIDs(people, _aya.Members);
        RecapState = new StepState.Working("Saving in Aya…");
        try
        {
            if (TargetMeetingID is { } target)
            {
                var current = await svc.RecordAsync(AyaSlot.Meetings, target);
                var values = RecapBuilder.RecapValues(_recapFields, rec, id, started, schema, _aya.Config, current, memberIDs, Tables);
                TargetRecord = await svc.Client.UpdateAsync(r.WorkspaceId, r.TechName, target, values);
                rec.RecapRecordID = target;
            }
            else
            {
                var values = RecapBuilder.RecapValues(_recapFields, rec, id, started, schema, _aya.Config, null, memberIDs, Tables);
                var created = await svc.Client.CreateAsync(r.WorkspaceId, r.TechName, values);
                rec.RecapRecordID = created.Id;
                TargetMeetingID = created.Id;
                TargetRecord = created;
            }
            rec.RecapUploadedAt = DateTime.UtcNow;
            Save(rec);
            RecapState = new StepState.Done($"Saved in Aya: “{TargetRecord?.Title ?? "meeting"}” ({r.Label}).");
        }
        catch (Exception e) when (e is AyaException or IOException) { RecapState = new StepState.Failed(e.Message); }
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
        if (await _aya.EnsureClaudeAsync() is not { } b) { CrmState = new StepState.Failed(ClaudeException.NotInstalled().Message); return; }
        CrmState = new StepState.Working("Claude is reading the CRM records and the transcript…");
        try
        {
            var client = await svc.RecordAsync(AyaSlot.Clients, clientID);
            var contacts = new List<AyaRecord>();
            if (_aya.Config.List(AyaSlot.Contacts) is not null)
                foreach (var c in rec.Context.Contacts)
                {
                    try { contacts.Add(await svc.RecordAsync(AyaSlot.Contacts, c.Id)); } catch (AyaException) { }
                }
            Crm = await _claude.ProposeCrmAsync(b, _aya.Settings.Model, rec, id, client, contacts, cs, _aya.Schema(AyaSlot.Contacts));
            CrmState = Crm.IsEmpty ? new StepState.Done("No changes proposed.") : StepState.IdleState;
        }
        catch (Exception e) when (e is ClaudeException or AyaException or IOException) { CrmState = new StepState.Failed(e.Message); }
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
        if (SessionId is null || Record is not { } rec || Crm is not { } p || _aya.Service is not { } svc
            || _aya.Config.List(AyaSlot.Clients) is not { } cRef || rec.Context.ClientID is not { } clientID) return;
        CrmState = new StepState.Working("Writing to Aya…");
        var done = new List<string>();
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
                        rec.CreatedContactIDs.Add(r.Id);
                        created += 1;
                    }
                }
                if (updated > 0) done.Add($"{updated} contact{(updated == 1 ? "" : "s")} updated");
                if (created > 0) done.Add($"{created} contact{(created == 1 ? "" : "s")} created");
            }
            rec.CrmAppliedAt = DateTime.UtcNow;
            Save(rec);
            Crm = null;
            CrmState = new StepState.Done(done.Count == 0 ? "Nothing selected." : "Saved in Aya: " + string.Join(", ", done) + ".");
        }
        catch (Exception e) when (e is AyaException or IOException)
        {
            if (rec.CreatedContactIDs.Count > 0) Save(rec);
            CrmState = new StepState.Failed(e.Message + (done.Count == 0 ? "" : $" (already saved: {string.Join(", ", done)})"));
        }
    }

    // MARK: Speakers (spec 02 §5.3, 03 §8.1)

    static Transcript? LoadTranscript(string id) =>
        JsonIO.TryRead<Transcript>(AppPaths.TranscriptFile(id), out var t) ? t : null;

    /// <summary>Gives the final transcript's speakers the names they had during the meeting, once per record.</summary>
    public void CarrySpeakerNames(string id)
    {
        if (SessionId != id) return;
        Transcript = LoadTranscript(id);
        if (Record is not { } rec || rec.SpeakersCarried == true) return;
        try
        {
            if (LoadTranscript(id) is not { } t) return;
            var names = SpeakerNames.CarryOver(t, rec.Lines, rec.State.Attendees);
            if (names.Count == 0) { Transcript = t; return; }
            foreach (var s in t.Speakers) if (names.TryGetValue(s.Id, out var n)) s.Label = n;
            try { JsonIO.Write(t, AppPaths.TranscriptFile(id)); } catch (IOException) { }
            Transcript = t;
        }
        finally
        {
            rec.SpeakersCarried = true;
            Save(rec);
        }
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
        if (sp is null) return;
        var old = sp.Label;
        if (old == n) return;
        sp.Label = n;
        try { JsonIO.Write(t, AppPaths.TranscriptFile(id)); } catch (IOException) { }
        Transcript = t;
        if (sp.Embedding is { } e && !sp.IsMe) _host.LearnSpeaker(n, e);
        if (SpeakerNames.IsGeneric(old))
            RecapFields = _recapFields.Select(f => f.Copy() with { Value = SpeakerNames.Replacing(old, n, f.Value) }).ToList();
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
