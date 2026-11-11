using System.Globalization;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Assist;

/// <summary>
/// The prepare screen's selections and the "What Claude will know" preview (spec 03 §5): a planned Aya
/// meeting, or template / client / project chosen freely, plus mode and goal.
/// </summary>
public sealed class AssistSetupModel : ObservableObject
{
    /// <summary>Debounce before the preview is loaded.</summary>
    public static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(300);
    /// <summary>Debounce before a server-side client search.</summary>
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);

    readonly AssistConnection _aya;
    readonly Func<string?, string?, string?, string?, Task<AyaMeetingContext>>? _loadContext;
    readonly TimeProvider _time;
    CancellationTokenSource? _previewCts;
    CancellationTokenSource? _searchCts;

    /// <param name="loadContext">For tests: replaces <see cref="AyaService.MeetingContextAsync"/>.</param>
    public AssistSetupModel(AssistConnection aya, Func<string?, string?, string?, string?, Task<AyaMeetingContext>>? loadContext = null,
                            TimeProvider? time = null)
    {
        _aya = aya;
        _loadContext = loadContext;
        _time = time ?? TimeProvider.System;
        aya.ListsRefreshed += DropVanished;
    }

    string? _meetingID, _clientID, _projectID, _templateID;
    string _goal = "";
    MeetingMode _mode = MeetingMode.Call;

    /// <summary>The planned meeting; picking one takes over its links, going back to "New meeting" drops them.</summary>
    public string? MeetingID
    {
        get => _meetingID;
        set { var old = _meetingID; if (Set(ref _meetingID, value)) MeetingChosen(old); }
    }
    public string? ClientID { get => _clientID; set { if (Set(ref _clientID, value)) LoadPreview(); } }
    public string? ProjectID { get => _projectID; set { if (Set(ref _projectID, value)) LoadPreview(); } }
    public string? TemplateID { get => _templateID; set { if (Set(ref _templateID, value)) LoadPreview(); } }
    public string Goal { get => _goal; set => Set(ref _goal, value); }
    public MeetingMode Mode { get => _mode; set => Set(ref _mode, value); }

    AyaMeetingContext? _preview;
    StepState _previewState = StepState.IdleState;
    public AyaMeetingContext? Preview { get => _preview; private set => Set(ref _preview, value); }
    public StepState PreviewState { get => _previewState; private set => Set(ref _previewState, value); }

    /// The selected planned meeting record (from the upcoming list).
    public AyaRecord? PlannedMeeting => MeetingID is { } id ? _aya.Upcoming.FirstOrDefault(m => m.Id == id) : null;

    /// Anything selected.
    public bool HasSelection => new[] { MeetingID, ClientID, ProjectID, TemplateID }.Any(x => x is not null);

    /// The client, project and template a meeting links to (through the configured link fields).
    public (string? Client, string? Project, string? Template) Links(AyaRecord m)
    {
        var c = _aya.Config;
        return (c.MeetingClientField is { } a ? m.LinkedID(a) : null,
                c.MeetingProjectField is { } b ? m.LinkedID(b) : null,
                c.MeetingTemplateField is { } t ? m.LinkedID(t) : null);
    }

    /// <summary>
    /// Picking a planned meeting copies its client, project and template. Going back to "New meeting" clears
    /// every selection that still has the value the planned meeting filled in; changed values are kept.
    /// </summary>
    void MeetingChosen(string? previous)
    {
        if (PlannedMeeting is { } m)
        {
            var (c, p, t) = Links(m);
            _clientID = c; _projectID = p; _templateID = t;
            Raise(nameof(ClientID)); Raise(nameof(ProjectID)); Raise(nameof(TemplateID));
        }
        else if (previous is not null && _aya.Upcoming.FirstOrDefault(x => x.Id == previous) is { } old)
        {
            var (c, p, t) = Links(old);
            if (_clientID == c) { _clientID = null; Raise(nameof(ClientID)); }
            if (_projectID == p) { _projectID = null; Raise(nameof(ProjectID)); }
            if (_templateID == t) { _templateID = null; Raise(nameof(TemplateID)); }
        }
        LoadPreview();
    }

    /// A selected template or planned meeting that disappeared after a refresh is deselected.
    void DropVanished()
    {
        if (TemplateID is { } t && !_aya.Templates.Any(x => x.Id == t)) TemplateID = null;
        if (MeetingID is { } m && !_aya.Upcoming.Any(x => x.Id == m)) MeetingID = null;
    }

    (string?, string?, string?, string?) SelectionKey => (MeetingID, ClientID, ProjectID, TemplateID);

    Task<AyaMeetingContext> Load(string? m, string? c, string? p, string? t)
    {
        if (_loadContext is not null) return _loadContext(m, c, p, t);
        var svc = _aya.Service ?? throw new AyaException(AyaErrorKind.NotConfigured);
        return svc.MeetingContextAsync(m, c, p, t);
    }

    /// <summary>Cancels a pending load; after 300 ms loads the context; results for an outdated selection are discarded.</summary>
    void LoadPreview()
    {
        _previewCts?.Cancel();
        var key = SelectionKey;
        if (!HasSelection || (_loadContext is null && _aya.Service is null))
        {
            Preview = null;
            PreviewState = StepState.IdleState;
            return;
        }
        PreviewState = new StepState.Working("Loading from Aya…");
        var cts = _previewCts = new CancellationTokenSource();
        _ = RunPreview(key, cts.Token);
    }

    async Task RunPreview((string? M, string? C, string? P, string? T) key, CancellationToken ct)
    {
        try { await Task.Delay(PreviewDelay, _time, ct); } catch (OperationCanceledException) { return; }
        try
        {
            var ctx = await Load(key.M, key.C, key.P, key.T);
            if (ct.IsCancellationRequested || SelectionKey != key) return;
            Preview = ctx;
            PreviewState = new StepState.Done();
        }
        catch (Exception e) when (e is AyaException or IOException)
        {
            if (ct.IsCancellationRequested) return;
            PreviewState = new StepState.Failed(e.Message);
        }
    }

    /// <summary>The context to start with: the loaded preview, a fresh load when it is missing, or an empty one without a selection.</summary>
    public async Task<AyaMeetingContext> ContextForStartAsync()
    {
        if (!HasSelection) return new AyaMeetingContext();
        if (Preview is { } p) return p;
        return await Load(MeetingID, ClientID, ProjectID, TemplateID);
    }

    /// <summary>Back to an empty selection (after a review).</summary>
    public void Reset()
    {
        _previewCts?.Cancel();
        _meetingID = _clientID = _projectID = _templateID = null;
        Goal = "";
        Raise(nameof(MeetingID)); Raise(nameof(ClientID)); Raise(nameof(ProjectID)); Raise(nameof(TemplateID));
        Preview = null;
        PreviewState = StepState.IdleState;
    }

    // MARK: Client search (spec 03 §5 client column)

    string _clientFilter = "";
    List<AyaRecord> _remoteClients = new();
    public string ClientFilter
    {
        get => _clientFilter;
        set { if (Set(ref _clientFilter, value)) { FilterChanged(value); Raise(nameof(FilteredClients)); } }
    }

    /// Local hits (case-insensitive contains), then remote hits not already present.
    public List<AyaRecord> FilteredClients
    {
        get
        {
            var f = AssistText.TrimWs(ClientFilter);
            if (f.Length == 0) return _aya.Clients;
            var local = _aya.Clients.Where(c => Contains(c.Title, f)).ToList();
            return local.Concat(_remoteClients.Where(r => !local.Any(l => l.Id == r.Id))).ToList();
        }
    }

    static bool Contains(string s, string f) =>
        CultureInfo.CurrentCulture.CompareInfo.IndexOf(s, f, CompareOptions.IgnoreCase) >= 0;

    void FilterChanged(string text)
    {
        _searchCts?.Cancel();
        _remoteClients = new();
        // Ask Aya only when the cached list has no match.
        if (text.Length < 2 || _aya.Clients.Any(c => Contains(c.Title, text))) return;
        var cts = _searchCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(SearchDelay, _time, cts.Token); } catch (OperationCanceledException) { return; }
            var found = await _aya.SearchClientsAsync(text);
            if (cts.IsCancellationRequested) return;
            _remoteClients = found;
            Raise(nameof(FilteredClients));
        });
    }

    // MARK: Card texts (spec 03 §5 planned strip)

    /// <summary>"Today 14:00", "Tomorrow 09:30", "Wed 12 Nov 10:00", or "No date".</summary>
    public static string PlannedDateText(DateTime? utc, DateTime? now = null)
    {
        if (utc is not { } d) return "No date";
        var local = d.ToLocalTime();
        var today = (now ?? DateTime.Now).Date;
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today) return $"Today {time}";
        if (local.Date == today.AddDays(1)) return $"Tomorrow {time}";
        return local.ToString("ddd d MMM", CultureInfo.CurrentCulture) + " " + time;
    }

    /// The meeting already has notes in Aya (summary, decisions or next steps).
    public static bool HasNotes(AyaRecord m) =>
        new[] { "summary__value", "decisions__value", "next_steps__value" }.Any(k => !m[k].IsEmptyValue());

    /// Number of checklist items of a template.
    public int ChecklistCount(AyaRecord template) =>
        PlaybookParser.Items(template[_aya.Config.TemplateAgendaField].AsString() ?? "").Count;
}
