using System.Globalization;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Claude;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Platform;

namespace MeetingAssistant.Core.Assist;

/// <summary>The Assist phase: prepare, a running meeting, or the review of a session (spec 03 §2).</summary>
public abstract record AssistPhase
{
    public sealed record Prepare : AssistPhase;
    public sealed record Running : AssistPhase;
    public sealed record Review(string SessionId) : AssistPhase;
}

/// <summary>
/// Assist mode for the UI (spec 03): the Aya connection, meeting preparation, the live assistant, the review
/// afterwards, the meetings panel and import. Owns the sub-controllers the views bind to.
/// </summary>
public sealed class AssistController : ObservableObject
{
    readonly IAssistHost _host;

    public AssistConnection Aya { get; }
    public AssistSetupModel Setup { get; }
    public AssistReviewController Review { get; }
    readonly ClaudeConversationFactory? _factory;

    AssistLiveSession? _live;
    /// The running meeting (null outside phase Running).
    public AssistLiveSession? Live { get => _live; private set => Set(ref _live, value); }

    AssistPhase _phase = new AssistPhase.Prepare();
    public AssistPhase Phase { get => _phase; private set { if (Set(ref _phase, value)) { Raise(nameof(CanStart)); Raise(nameof(ReviewSessionId)); } } }

    StepState _startState = StepState.IdleState;
    public StepState StartState { get => _startState; private set { if (Set(ref _startState, value)) Raise(nameof(CanStart)); } }

    /// <summary>The session under review (phase Review), else null.</summary>
    public string? ReviewSessionId => (Phase as AssistPhase.Review)?.SessionId;

    /// <summary>Call when the app's recording state changed: <see cref="CanStart"/> depends on it.</summary>
    public void RecordingStateChanged() => Raise(nameof(CanStart));

    /// <summary>Tooltip of "Start Assist" (spec 03 §5 footer).</summary>
    public string StartTooltip => _host.IsRecording ? "A recording is already running." : "Starts the recording with live transcript and the assistant.";

    /// <param name="secrets">Where the Aya API key is kept (Windows: Credential Manager).</param>
    /// <param name="factory">For tests: replaces the Claude process of the live engine.</param>
    public AssistController(IAssistHost host, ISecretStore secrets, IPreferenceStore? prefs = null, IRecapClaude? recapClaude = null,
                            ClaudeConversationFactory? factory = null)
    {
        _host = host;
        _factory = factory;
        Aya = new AssistConnection(secrets, prefs);
        Setup = new AssistSetupModel(Aya);
        Review = new AssistReviewController(host, Aya, recapClaude);
        Aya.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AssistConnection.ClaudeBinary) or nameof(AssistConnection.ClaudeAuth)) Raise(nameof(CanStart)); };
        Setup.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AssistSetupModel.PreviewState)) Raise(nameof(CanStart)); };
        Aya.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AssistConnection.HasApiKey) or nameof(AssistConnection.Config)) Raise(nameof(CanStart)); };
    }

    /// <summary>At app start: refresh or upgrade the Aya lists in the background.</summary>
    public Task StartupAsync() => Aya.StartupAsync();

    /// <summary>
    /// Start is allowed when Aya is connected, Claude is ready, no recording runs, the phase is Prepare, start
    /// is not already running and the preview is not loading (spec 03 §5).
    /// </summary>
    public bool CanStart => Aya.IsConnected && Aya.ClaudeReady && !_host.IsRecording && Phase is AssistPhase.Prepare
                            && !StartState.IsWorking && !Setup.PreviewState.IsWorking;

    /// <summary>Start Assist: starts the recording and the engine; switches to Running.</summary>
    public async Task StartAsync()
    {
        if (Aya.Service is null) return;
        var live = new AssistLiveSession(_host, Aya, _factory);
        live.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AssistLiveSession.StartState)) StartState = live.StartState; };
        StartState = new StepState.Working("Loading the meeting context from Aya…");
        if (!await live.StartAsync(Setup)) { StartState = live.StartState; return; }
        StartState = StepState.IdleState;
        live.Finished += id =>
        {
            Live = null;
            if (id is null) Phase = new AssistPhase.Prepare(); else OpenReview(id);
        };
        Live = live;
        Phase = new AssistPhase.Running();
    }

    /// <summary>Stops the running meeting's recording; the review opens when it has stopped.</summary>
    public Task StopAsync() => Live?.StopAsync() ?? Task.CompletedTask;

    /// <summary>Opens the review of a session (always switches to Assist mode).</summary>
    public void OpenReview(string id)
    {
        if (Review.Open(id)) Phase = new AssistPhase.Review(id);
    }

    /// <summary>Done: flush the draft, reset everything and reload the planned meetings.</summary>
    public void FinishReview()
    {
        Review.Finish();
        Setup.Reset();
        Phase = new AssistPhase.Prepare();
        _ = Aya.RefreshUpcomingAsync();
    }

    /// <summary>Call when the app quits: writes a pending recap draft.</summary>
    public void OnQuit() => Review.FlushDraft();

    // MARK: Meetings panel (spec 03 §9.1)

    /// <summary>Sessions that have an Assist record, excluding the one recording; newest first; filtered by title.</summary>
    public List<SessionInfo> Meetings(string filter = "")
    {
        var recording = Live?.SessionId;
        var f = filter.Trim();
        return _host.Sessions.Where(s => s.Id != recording && File.Exists(AppPaths.AssistFile(s.Id)))
            .Where(s => f.Length == 0 || CultureInfo.CurrentCulture.CompareInfo.IndexOf(s.Title, f, CompareOptions.IgnoreCase) >= 0)
            .ToList();
    }

    /// <summary>Busy: recording, queued or transcribing. Delete is disabled for busy sessions.</summary>
    public static bool IsBusy(SessionInfo? s) => s is null || s.Status is SessionStatus.Recording or SessionStatus.Queued or SessionStatus.Transcribing;

    /// <summary>"Delete “title”?" dialog title.</summary>
    public static string DeleteTitle(string title) => $"Delete “{title}”?";
    public const string DeleteMessage = "The recording, the transcript and the notes on this PC are deleted. What was saved in Aya stays there.";

    /// <summary>Deletes a meeting; the one under review is finished first (spec 03 §8.7).</summary>
    public void DeleteMeeting(string id)
    {
        if (IsBusy(_host.Session(id))) return;
        if (Review.SessionId == id) FinishReview();
        _host.DeleteSession(id);
    }

    // MARK: Import (spec 03 §9.2)

    /// <summary>
    /// Imports one or two recordings as an Assist meeting, linked to a meeting in Aya or with client, project
    /// and template for a new one. It is transcribed, then the review drafts the recap.
    /// </summary>
    public async Task ImportAsync(IReadOnlyList<string> files, MeetingMode mode, DateTime date, string title, string? meetingID,
                                  string? clientID, string? projectID, string? templateID)
    {
        if (files.Count == 0) return;
        var aya = new AyaMeetingContext();
        if (new[] { meetingID, clientID, projectID, templateID }.Any(x => x is not null))
        {
            var svc = Aya.Service ?? throw new AyaException(AyaErrorKind.NotConfigured);
            aya = meetingID is not null
                ? await svc.MeetingContextAsync(meetingID)
                : await svc.MeetingContextAsync(null, clientID, projectID, templateID);
        }
        var me = _host.MyName.Trim();
        var effectiveMode = files.Count > 1 ? (mode == MeetingMode.InPerson ? MeetingMode.Call : mode) : MeetingMode.InPerson;
        var ctx = AssistContext.FromAya(aya, Aya.Config, "", effectiveMode, me.Length == 0 ? "Me" : me, date);
        var name = title.Trim();
        if (name.Length > 0) ctx.MeetingTitle = name;      // a typed title wins
        var sessionTitle = ctx.Title == "Meeting" ? Path.GetFileNameWithoutExtension(files[0]) : ctx.Title;
        var info = await _host.ImportFilesAsync(files[0], files.Count > 1 ? files[1] : null, effectiveMode, sessionTitle, date);
        var st = new AssistState { Playbook = ctx.PlaybookItems, Tables = ctx.TemplateTables };
        new AssistRecord(ctx, st, Array.Empty<AssistLine>()).Save(info.Id);
        _host.ReloadSessions();
        _host.Enqueue(info.Id);
        OpenReview(info.Id);
    }
}
