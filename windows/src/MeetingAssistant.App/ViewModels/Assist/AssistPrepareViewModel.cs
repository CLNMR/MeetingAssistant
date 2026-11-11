using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ObservableObject = CommunityToolkit.Mvvm.ComponentModel.ObservableObject;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Controls;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Json;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.App.ViewModels;

/// A card in the "Planned in Aya" strip (spec 03 §5): "New meeting" (Id null) or a planned meeting.
public sealed record PlannedCard(string? Id, string TopLine, string Title, string? Links, bool HasNotes, bool IsSelected, string Tooltip)
{
    public bool IsNew => Id is null;
}

/// A row of the client column: "No client" (Id null) or a client.
public sealed record ClientRow(string? Id, string Title, string Subtitle, string Initial, bool IsSelected)
{
    public bool IsNone => Id is null;
}

/// A contact chip in the preview: name plus optional detail.
public sealed record NameChip(string Name, string? Detail);

/// An earlier meeting in the preview: date (yyyy-MM-dd) and title.
public sealed record EarlierMeeting(string Date, string Title);

/// Assist "Prepare meeting" screen (spec 03 §5): planned strip, client column, template/project, mode and
/// goal, the "What Claude will know" preview and the start footer. Logic lives in <see cref="AssistSetupModel"/>.
public sealed partial class AssistPrepareViewModel : ScreenViewModel
{
    readonly AssistController _assist;
    readonly DispatcherTimer _clock;

    public AssistPrepareViewModel(AssistController assist)
    {
        _assist = assist;
        AssistUiHelpers.Observe(assist.Aya, p =>
        {
            switch (p)
            {
                case nameof(AssistConnection.Upcoming): OnPropertyChanged(nameof(PlannedCards)); break;
                case nameof(AssistConnection.Clients): OnPropertyChanged(nameof(ClientRows)); OnPropertyChanged(nameof(ClientCount)); break;
                case nameof(AssistConnection.Templates): OnPropertyChanged(nameof(TemplateChoices)); TemplateChanged(); break;
                case nameof(AssistConnection.Projects): OnPropertyChanged(nameof(ProjectChoices)); OnPropertyChanged(nameof(ProjectStatus)); break;
                case nameof(AssistConnection.ListsFetchedAt): OnPropertyChanged(nameof(ListsUpdatedText)); break;
                case nameof(AssistConnection.ListState): RefreshCommand.NotifyCanExecuteChanged(); break;
                case nameof(AssistConnection.Config): OnPropertyChanged(nameof(HasProjectList)); OnPropertyChanged(nameof(PlannedCards)); break;
                case nameof(AssistConnection.ClaudeState):
                case nameof(AssistConnection.Settings):
                    OnPropertyChanged(nameof(ClaudeFailed)); OnPropertyChanged(nameof(ClaudeChecking)); OnPropertyChanged(nameof(ClaudeLine)); break;
            }
        });
        AssistUiHelpers.Observe(assist.Setup, p =>
        {
            switch (p)
            {
                case nameof(AssistSetupModel.MeetingID): OnPropertyChanged(nameof(PlannedCards)); OnPropertyChanged(nameof(HasPlannedMeeting)); break;
                case nameof(AssistSetupModel.ClientID): OnPropertyChanged(nameof(ClientRows)); break;
                case nameof(AssistSetupModel.FilteredClients): OnPropertyChanged(nameof(ClientRows)); break;
                case nameof(AssistSetupModel.TemplateID): OnPropertyChanged(nameof(TemplateID)); TemplateChanged(); break;
                case nameof(AssistSetupModel.ProjectID): OnPropertyChanged(nameof(ProjectID)); OnPropertyChanged(nameof(ProjectStatus)); break;
                case nameof(AssistSetupModel.Preview): PreviewChanged(); break;
                case nameof(AssistSetupModel.PreviewState): OnPropertyChanged(nameof(ShowEmptyPreview)); break;
            }
        });
        AssistUiHelpers.Observe(assist, p =>
        {
            if (p is nameof(AssistController.CanStart)) { StartCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(StartTooltip)); }
            if (p is nameof(AssistController.StartState)) StartCommand.NotifyCanExecuteChanged();
        });
        // "Aya lists updated 5 min ago" ages while the screen is open.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => OnPropertyChanged(nameof(ListsUpdatedText));
    }

    public override string ScreenName => "Prepare";
    public AssistController Assist => _assist;
    public AssistSetupModel Setup => _assist.Setup;
    public AssistConnection Aya => _assist.Aya;

    /// Every time the screen appears: reload the planned meetings and check Claude if unknown.
    public void Appeared()
    {
        _clock.Start();
        _ = Aya.RefreshUpcomingAsync();
        if (Aya.ClaudeBinary is null && !Aya.ClaudeState.IsWorking) _ = Aya.CheckClaudeAsync();
    }

    public void Disappeared() => _clock.Stop();

    // MARK: Header

    /// "Aya lists updated &lt;relative time&gt;" once lists were fetched, else null.
    public string? ListsUpdatedText => Aya.ListsFetchedAt is { } d ? "Aya lists updated " + AssistUi.RelativeTime(d, DateTime.UtcNow) : null;

    bool CanRefresh() => !Aya.ListState.IsWorking;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    Task Refresh() => Aya.RefreshListsAsync();

    // MARK: Planned strip

    public bool HasPlannedMeeting => Setup.MeetingID is not null;

    public IReadOnlyList<PlannedCard> PlannedCards
    {
        get
        {
            var cfg = Aya.Config;
            var list = new List<PlannedCard>
            {
                new(null, "New meeting", "Choose template, client and project yourself", null, false, Setup.MeetingID is null,
                    "A new meeting is created in Aya afterwards"),
            };
            foreach (var m in Aya.Upcoming)
            {
                var links = new[] { cfg.MeetingClientField, cfg.MeetingProjectField, cfg.MeetingTemplateField }
                    .Where(f => f is not null).Select(f => m.LinkedTitle(f!)).Where(t => !string.IsNullOrEmpty(t)).ToList();
                var notes = AssistSetupModel.HasNotes(m);
                list.Add(new(m.Id, AssistSetupModel.PlannedDateText(m.Date(cfg.MeetingDateField)), m.Title,
                    links.Count == 0 ? null : string.Join(" · ", links), notes, Setup.MeetingID == m.Id,
                    m.Title + (notes ? " · already has notes in Aya" : "")));
            }
            return list;
        }
    }

    [RelayCommand]
    void PickPlanned(string? id) => Setup.MeetingID = id;

    // MARK: Client column

    /// Bound to the search field.
    public string ClientFilter
    {
        get => Setup.ClientFilter;
        set { if (Setup.ClientFilter != value) { Setup.ClientFilter = value; OnPropertyChanged(); OnPropertyChanged(nameof(ClientRows)); } }
    }

    public int ClientCount => Aya.Clients.Count;

    public IReadOnlyList<ClientRow> ClientRows
    {
        get
        {
            var rows = new List<ClientRow>();
            if (Setup.ClientFilter.Length == 0)
                rows.Add(new(null, "No client", "Internal meeting, or project only", "", Setup.ClientID is null));
            foreach (var c in Setup.FilteredClients)
            {
                var sub = string.Join(" · ", new[] { "branche__value", "domain__value", "region__value" }
                    .Select(k => c[k].AsString() ?? (c[k] is { } n && !n.IsEmptyValue() ? n.Describe() : null))
                    .Where(s => !string.IsNullOrEmpty(s)));
                rows.Add(new(c.Id, c.Title, sub, AssistUi.Initial(c.Title), Setup.ClientID == c.Id));
            }
            return rows;
        }
    }

    [RelayCommand]
    void PickClient(string? id) => Setup.ClientID = id;

    // MARK: Template, project, meeting

    public IReadOnlyList<Choice> TemplateChoices =>
        new[] { new Choice(null, "No template") }.Concat(Aya.Templates.Select(t => new Choice(t.Id, t.Title))).ToList();

    public object? TemplateID { get => Setup.TemplateID; set => Setup.TemplateID = value as string; }

    AyaRecord? Template => Setup.TemplateID is { } id ? Aya.Templates.FirstOrDefault(t => t.Id == id) : null;
    public string? TemplateType => Template?[Aya.Config.TemplateTypeField].AsString() is { Length: > 0 } s ? s : null;
    public string? TemplateItemsText => Template is { } t && Setup.ChecklistCount(t) is > 0 and var n ? $"{n} checklist items" : null;
    public string? TemplateGoal => Template?[Aya.Config.TemplateGoalField].AsString() is { Length: > 0 } s ? s : null;

    void TemplateChanged()
    {
        OnPropertyChanged(nameof(TemplateType));
        OnPropertyChanged(nameof(TemplateItemsText));
        OnPropertyChanged(nameof(TemplateGoal));
    }

    public bool HasProjectList => Aya.Config.List(AyaSlot.Projects) is not null;

    public IReadOnlyList<Choice> ProjectChoices =>
        new[] { new Choice(null, "No project") }.Concat(Aya.Projects.Select(p => new Choice(p.Id, p.Title))).ToList();

    public object? ProjectID { get => Setup.ProjectID; set => Setup.ProjectID = value as string; }

    public string? ProjectStatus => Setup.ProjectID is { } id && Aya.Projects.FirstOrDefault(p => p.Id == id)?["status__value"].AsString() is { Length: > 0 } s ? s : null;

    /// Call | In person | Hybrid.
    public IReadOnlyList<Segment> Modes { get; } = Enum.GetValues<MeetingMode>().Select(m => new Segment(m, m.Title())).ToList();

    // MARK: Preview

    public bool ShowEmptyPreview => Setup.Preview is null && !Setup.PreviewState.IsWorking;
    public bool HasPreview => Setup.Preview is not null;
    public string? PreviewMeeting => Setup.Preview?.Meeting?.Title;
    public string? PreviewClient => Setup.Preview?.Client?.Title;
    public string? PreviewClientProfile =>
        Setup.Preview?.Client?.Values.FirstOrDefault(kv => kv.Key.Contains("profil")).Value.AsString() is { Length: > 0 } s ? s : null;
    public string? PreviewProject => Setup.Preview?.Project?.Title;
    public string? PreviewTemplate => Setup.Preview?.Template?.Title;
    public string? PreviewTemplateItems
    {
        get
        {
            if (Setup.Preview?.Template is not { } t) return null;
            var items = PlaybookParser.Items(t[Aya.Config.TemplateAgendaField].AsString() ?? "");
            if (items.Count == 0) return null;
            return string.Join(" · ", items.Take(6).Select(i => i.Text)) + (items.Count > 6 ? " …" : "");
        }
    }
    public IReadOnlyList<NameChip> PreviewContacts =>
        Setup.Preview?.Contacts.Select(c => new NameChip(c.Title, c["position__value"].AsString())).ToList() ?? new List<NameChip>();
    public IReadOnlyList<EarlierMeeting> PreviewRecaps =>
        Setup.Preview?.Recaps.Select(r =>
        {
            var d = r[Aya.Config.MeetingDateField].AsString() ?? r["createdAt"].AsString() ?? "";
            return new EarlierMeeting(d.Length > 10 ? d[..10] : d, r.Title);
        }).ToList() ?? new List<EarlierMeeting>();
    public bool NoRecaps => PreviewRecaps.Count == 0;

    void PreviewChanged()
    {
        foreach (var n in new[] { nameof(ShowEmptyPreview), nameof(HasPreview), nameof(PreviewMeeting), nameof(PreviewClient), nameof(PreviewClientProfile),
                                  nameof(PreviewProject), nameof(PreviewTemplate), nameof(PreviewTemplateItems), nameof(PreviewContacts),
                                  nameof(PreviewRecaps), nameof(NoRecaps) })
            OnPropertyChanged(n);
    }

    // MARK: Footer

    public bool ClaudeFailed => Aya.ClaudeState is StepState.Failed;
    public bool ClaudeChecking => Aya.ClaudeState.IsWorking;
    public string ClaudeLine => Aya.ClaudeLine;

    [RelayCommand]
    Task CheckClaude() => Aya.CheckClaudeAsync();

    /// Tooltip of Start Assist.
    public string StartTooltip => _assist.StartTooltip;

    [RelayCommand(CanExecute = nameof(CanStartNow))]
    Task Start() => _assist.StartAsync();

    bool CanStartNow() => _assist.CanStart;
}
