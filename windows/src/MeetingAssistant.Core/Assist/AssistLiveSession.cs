using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Claude;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Assist;

/// <summary>What Aya knows about a person (person popover, spec 03 §6.7).</summary>
public sealed record PersonCard(string Name, string Source, List<LabeledValue> Details);

/// <summary>People lookups shared by the live view and the review (spec 03 §6.6–6.7).</summary>
public static class AssistPeople
{
    /// <summary>A contact of this meeting's client (by id, else by name), else a team member by name.</summary>
    public static PersonCard? Card(string name, string? contactID, AssistContext? context, IReadOnlyList<AyaRecord> members, AyaListSchema? memberSchema)
    {
        var contacts = context?.Contacts ?? new();
        var client = context?.ClientTitle ?? "";
        var c = contacts.FirstOrDefault(x => x.Id == contactID) ?? PersonMatch.Match(name, contacts, x => x.Name);
        if (c is not null)
            return new PersonCard(c.Name, client.Length == 0 ? "Contact in Aya" : $"Contact at {client}", PersonInfo.Important(c.Details));
        if (PersonMatch.Match(name, members, m => m.Title) is { } m)
            return new PersonCard(m.Title, "Team member in Aya", PersonInfo.Important(AssistContext.LabeledValues(m, memberSchema)));
        return null;
    }

    /// <summary>A contact that is not part of the meeting context, loaded from Aya by id.</summary>
    public static async Task<PersonCard?> LoadContactAsync(AyaService? svc, string id, AyaListSchema? contactSchema)
    {
        if (svc is null) return null;
        try
        {
            var r = await svc.RecordAsync(AyaSlot.Contacts, id);
            return new PersonCard(r.Title, "Contact in Aya", PersonInfo.Important(AssistContext.LabeledValues(r, contactSchema)));
        }
        catch (AyaException) { return null; }
    }

    /// <summary>Names to choose from when renaming: attendees, the client's contacts, then the team; no generic names, not me, no duplicates.</summary>
    public static List<string> NameSuggestions(AssistState state, AssistContext? context, IReadOnlyList<AyaRecord> members, string myName)
    {
        var output = new List<string>();
        var names = state.Attendees.Select(a => a.Name).Concat(context?.Contacts.Select(c => c.Name) ?? Enumerable.Empty<string>())
            .Concat(members.Select(m => m.Title));
        foreach (var n in names) if (!SpeakerNames.IsGeneric(n) && n != myName && !output.Contains(n)) output.Add(n);
        return output;
    }

    /// <summary>The popover subtitle: source, " · speaks as “label”" when it differs, or "Not named yet" (spec 03 §6.7).</summary>
    public static string Subtitle(string name, string? label, PersonCard? card)
    {
        var parts = card?.Source ?? (!SpeakerNames.IsGeneric(name) ? "Not found in Aya" : "");
        if (label is not null && label != name) parts += (parts.Length > 0 ? " · " : "") + $"speaks as “{label}”";
        return parts.Length == 0 ? "Not named yet" : parts;
    }
}

/// <summary>
/// One running Assist meeting (spec 03 §5 start flow, §6, §7): starts the recording through the host, feeds
/// live lines to the engine, exposes panel state and status, and the user actions.
/// </summary>
public sealed class AssistLiveSession : ObservableObject
{
    /// <summary>Lines kept for the live transcript panel.</summary>
    public const int VisibleLines = 300;

    readonly IAssistHost _host;
    readonly AssistConnection _aya;
    readonly ClaudeConversationFactory? _factory;
    readonly SynchronizationContext? _ui;
    AssistEngine? _engine;
    long _sequence, _latest;

    /// <param name="factory">For tests: replaces the real Claude process.</param>
    public AssistLiveSession(IAssistHost host, AssistConnection aya, ClaudeConversationFactory? factory = null)
    {
        _host = host;
        _aya = aya;
        _factory = factory;
        _ui = SynchronizationContext.Current;
    }

    AssistState _state = new();
    AssistStatus _status = new AssistStatus.Idle();
    bool _paused;
    StepState _startState = StepState.IdleState;
    IReadOnlyList<LiveLine> _liveLines = Array.Empty<LiveLine>();

    public AssistState State { get => _state; private set => Set(ref _state, value); }
    public AssistStatus Status { get => _status; private set => Set(ref _status, value); }
    public bool Paused { get => _paused; private set => Set(ref _paused, value); }
    public StepState StartState { get => _startState; private set => Set(ref _startState, value); }
    /// The last <see cref="VisibleLines"/> live lines.
    public IReadOnlyList<LiveLine> LiveLines { get => _liveLines; private set => Set(ref _liveLines, value); }
    public AssistContext? Context { get; private set; }
    public string? SessionId { get; private set; }
    public bool IsRunning => _engine is not null;

    /// <summary>Raised once when the recording stopped: the session id to review, or null to return to Prepare.</summary>
    public event Action<string?>? Finished;

    void OnUi(Action a)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) a(); else _ui.Post(_ => a(), null);
    }

    /// <summary>
    /// Start flow (spec 03 §5): build the context, start the recording with the live hook, save the skeleton
    /// record, reset the panels and start the engine. Returns false (with <see cref="StartState"/> failed) when it did not start.
    /// </summary>
    public async Task<bool> StartAsync(AssistSetupModel setup)
    {
        await _aya.EnsureClaudeAsync();
        if (_aya.ClaudeBinary is not { } binary || !_aya.ClaudeReady) { StartState = new StepState.Failed("Claude Code is not ready."); return false; }
        StartState = new StepState.Working("Loading the meeting context from Aya…");
        try
        {
            var aya = await setup.ContextForStartAsync();
            var me = _host.MyName.Trim();
            var ctx = AssistContext.FromAya(aya, _aya.Config, setup.Goal, setup.Mode, me.Length == 0 ? "Me" : me, DateTime.UtcNow);
            return await StartAsync(ctx, binary);
        }
        catch (Exception e) when (e is AyaException or IOException)
        {
            StartState = new StepState.Failed(e.Message);
            return false;
        }
    }

    /// <summary>Starts the meeting for a ready context (also used by tests).</summary>
    public async Task<bool> StartAsync(AssistContext ctx, string binary)
    {
        Context = ctx;
        StartState = new StepState.Working("Starting the recording…");
        var (sid, error) = await _host.StartRecordingAsync(ctx.Mode, ctx.Title, Ingest, RecordingStopped);
        if (sid is null)
        {
            StartState = new StepState.Failed(error ?? "The recording did not start.");
            return false;
        }
        SessionId = sid;
        try { new AssistRecord(ctx, new AssistState(), Array.Empty<AssistLine>()).Save(sid); } catch (IOException) { }

        var initial = new AssistState { Playbook = ctx.PlaybookItems.Select(p => p with { }).ToList(), Tables = ctx.TemplateTables };
        State = initial;
        Status = new AssistStatus.Starting();
        Paused = false;
        StartState = StepState.IdleState;
        _engine = new AssistEngine(ctx, _aya.Settings, _factory ?? ClaudeStream.Factory(binary), (s, st) =>
        {
            var n = Interlocked.Increment(ref _sequence);
            OnUi(() =>
            {
                // Updates that arrive out of order are dropped.
                if (n < _latest) return;
                _latest = n;
                State = s;
                Status = st;
            });
        });
        await _engine.StartAsync();
        return true;
    }

    void Ingest(IReadOnlyList<LiveLine> lines)
    {
        var shown = lines.Count > VisibleLines ? lines.Skip(lines.Count - VisibleLines).ToList() : lines;
        OnUi(() => LiveLines = shown);
        _ = _engine?.IngestAsync(lines);
    }

    // MARK: During the meeting

    public Task MarkAskedAsync(string id) => _engine?.MarkAskedAsync(id) ?? Task.CompletedTask;
    public Task DismissAsync(string id) => _engine?.DismissAsync(id) ?? Task.CompletedTask;
    public Task PromoteAsync(string id) => _engine?.PromoteAsync(id) ?? Task.CompletedTask;
    public Task SuggestNowAsync() => _engine?.SuggestNowAsync() ?? Task.CompletedTask;
    public Task SetOpenPointAsync(string id, OpenPointStatus s) => _engine?.SetOpenPointAsync(id, s) ?? Task.CompletedTask;
    public Task SetPlaybookAsync(string id, PlaybookStatus s) => _engine?.SetPlaybookAsync(id, s) ?? Task.CompletedTask;
    public Task SetTableCellAsync(string row, string column, string value) => _engine?.SetTableCellAsync(row, column, value) ?? Task.CompletedTask;
    public Task RenameSpeakerAsync(string raw, string name) => _engine?.RenameSpeakerAsync(raw, name) ?? Task.CompletedTask;

    /// <summary>Pauses or resumes the assistant; the recording keeps running.</summary>
    public Task TogglePauseAsync()
    {
        Paused = !Paused;
        return _engine?.SetPausedAsync(Paused) ?? Task.CompletedTask;
    }

    /// <summary>Stops the recording; <see cref="Finished"/> follows.</summary>
    public Task StopAsync() => _host.StopRecordingAsync();

    // MARK: "What did I miss?" (spec 02 §3.8, 03 §6.2)

    (int Minutes, string Text)? _catchUp;
    StepState _catchUpState = StepState.IdleState;
    public (int Minutes, string Text)? CatchUp { get => _catchUp; private set => Set(ref _catchUp, value); }
    public StepState CatchUpState { get => _catchUpState; private set => Set(ref _catchUpState, value); }
    public int CatchUpMinutes { get; private set; }

    /// <summary>Summarises the last 2, 5 or 10 minutes with haiku, effort low, 60 s timeout.</summary>
    public async Task WhatDidIMissAsync(int minutes)
    {
        if (_aya.ClaudeBinary is not { } binary || _engine is not { } e) return;
        CatchUpMinutes = minutes;
        var lines = await e.GetTranscriptAsync();
        var prompt = AssistPrompts.CatchUpPrompt(lines, minutes, Context?.MyName ?? "Me", Context?.Title ?? "");
        if (prompt is null) { CatchUpState = new StepState.Failed("Nothing was said yet."); return; }
        CatchUpState = new StepState.Working($"Catching up on the last {minutes} minutes…");
        try
        {
            var r = await ClaudeCli.OneShotAsync(binary, AssistPrompts.CatchUpSystem, prompt, "haiku", "low", timeoutSeconds: ClaudeCli.CatchUpTimeoutSeconds);
            CatchUp = (minutes, AssistText.TrimAll(r.Text));
            CatchUpState = StepState.IdleState;
        }
        catch (ClaudeException x) { CatchUpState = new StepState.Failed(x.Message); }
    }

    /// <summary>Closes the catch-up card.</summary>
    public void CloseCatchUp() { CatchUp = null; CatchUpState = StepState.IdleState; }

    // MARK: Speakers

    /// <summary>The name shown for a live line (spec 03 §6.6): the assigned name resolved to an attendee, else my name for "me", else null (unknown).</summary>
    public string? LiveName(LiveLine l)
    {
        if (l.Speaker is not { } raw) return null;
        var me = _host.MyName.Length == 0 ? "Me" : _host.MyName;
        if (State.SpeakerNames?.TryGetValue(raw, out var n) == true) return SpeakerNames.Resolved(n, State.Attendees);
        return raw == "me" ? me : null;
    }

    /// <summary>The live label ("system-1") an attendee speaks under, when known.</summary>
    public string? RawSpeaker(Attendee a) =>
        a.Speaker is { } s ? State.SpeakerNames?.FirstOrDefault(kv => kv.Value == s).Key : null;

    public List<string> NameSuggestions => AssistPeople.NameSuggestions(State, Context, _aya.Members, _host.MyName);

    public PersonCard? PersonCard(string name, string? contactID = null) =>
        AssistPeople.Card(name, contactID, Context, _aya.Members, _aya.Schema(AyaSlot.Members));

    // MARK: Stop (spec 03 §7)

    void RecordingStopped(string id)
    {
        var e = _engine;
        _engine = null;
        _ = Task.Run(async () =>
        {
            AssistState final = State;
            List<AssistLine> lines = new();
            if (e is not null)
            {
                final = await e.StopAsync();
                lines = await e.GetTranscriptAsync();
            }
            string? review = null;
            if (id.Length > 0 && Context is { } ctx)
            {
                try { new AssistRecord(ctx, final, lines).Save(id); review = id; } catch (IOException) { }
            }
            OnUi(() =>
            {
                State = final;
                Finished?.Invoke(review);
            });
        });
    }
}
