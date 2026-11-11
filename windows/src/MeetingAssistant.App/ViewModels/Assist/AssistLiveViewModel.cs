using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MeetingAssistant.App.Controls;
using ObservableObject = CommunityToolkit.Mvvm.ComponentModel.ObservableObject;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Infrastructure;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Capture;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Storage;

namespace MeetingAssistant.App.ViewModels;

/// A summary paragraph: bold topic (optional) and the rest; highlighted when the last update changed it.
public sealed record SummaryItem(int Index, string? Topic, string Body, bool IsChanged);

/// An open point row (spec 03 §6.4).
public sealed record OpenPointItem(string Id, string Text, OpenPointStatus Status, bool IsChanged)
{
    public bool IsAnswered => Status == OpenPointStatus.Answered;
    public bool IsParked => Status == OpenPointStatus.Parked;
    public string Tooltip => IsAnswered ? "Mark open" : "Mark answered";
}

/// A checklist row; <see cref="SectionHeader"/> is set when the section changes.
public sealed record ChecklistItem(string Id, string? SectionHeader, string Text, PlaybookStatus Status, string? Evidence, bool IsChanged)
{
    public bool IsCovered => Status == PlaybookStatus.Covered;
    public bool IsPartial => Status == PlaybookStatus.Partial;
}

/// A suggested question (alternatives are promoted on click).
public sealed record QuestionItem(string Id, string Text);

/// A summary tile of one template table during the meeting (spec 03 §6.5).
public sealed record TableTile(string Id, string Name, string FilledText, double Fraction, IReadOnlyList<TableTileLine> Latest);
public sealed record TableTileLine(string Text, bool IsChanged);

/// An attendee chip (spec 03 §6.6).
public sealed record AttendeeChip(Attendee Attendee, string Initial, string? Role, string? SpeakerSuffix, bool IsKnown, string Tooltip, bool IsChanged, bool Dim)
{
    public string Name => Attendee.Name;
    public string KnownTooltip => IsKnown ? "Known contact in Aya" : "Not in Aya yet";
}

/// A live transcript line with its speaker button (spec 03 §6.6).
public sealed record LiveLineItem(LiveLine Line, string? SpeakerName, bool IsMic, bool IsGeneric, string SpeakerTooltip)
{
    public string Text => Line.Text;
    public bool IsFinal => Line.IsFinal;
    public bool HasSpeaker => SpeakerName is not null;
}

/// <summary>
/// Assist live screen (spec 03 §6): status bar, "What did I miss?", next question, Summary / Open points /
/// Checklist with resizable widths, template tables, attendees and the live transcript. Binds to one
/// <see cref="AssistLiveSession"/>; elapsed time and levels come from the recording through <paramref name="meter"/>.
/// </summary>
public sealed partial class AssistLiveViewModel : ScreenViewModel
{
    readonly AssistController _assist;
    readonly AssistLiveSession _live;
    readonly IUiState _ui;
    readonly Func<(double Elapsed, float Mic, float System)> _meter;
    readonly DispatcherTimer _timer;

    public AssistLiveViewModel(AssistController assist, AssistLiveSession live, IUiState ui, Func<(double, float, float)> meter)
    {
        _assist = assist;
        _live = live;
        _ui = ui;
        _meter = meter;
        AssistUiHelpers.Observe(live, p =>
        {
            switch (p)
            {
                case nameof(AssistLiveSession.State): StateChanged(); break;
                case nameof(AssistLiveSession.Status): StatusChanged(); break;
                case nameof(AssistLiveSession.Paused): OnPropertyChanged(nameof(Paused)); OnPropertyChanged(nameof(ListeningText)); OnPropertyChanged(nameof(PauseTitle)); break;
                case nameof(AssistLiveSession.LiveLines): OnPropertyChanged(nameof(Lines)); break;
                case nameof(AssistLiveSession.CatchUp):
                case nameof(AssistLiveSession.CatchUpState): CatchUpChanged(); break;
            }
        });
        _summaryShare = ui.Get(UiKeys.SummaryShare, LiveLayout.DefaultSummaryShare);
        _openShare = ui.Get(UiKeys.OpenShare, LiveLayout.DefaultOpenShare);
        _transcriptHeight = ui.Get(UiKeys.TranscriptHeight, LiveLayout.DefaultTranscriptHeight);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
    }

    public override string ScreenName => "Live";
    public AssistLiveSession Session => _live;
    AssistState State => _live.State;

    public void Appeared() { _timer.Start(); Tick(); }
    public void Disappeared() => _timer.Stop();

    // MARK: Status bar (spec 03 §6.1)

    [ObservableProperty] string _elapsedText = "00:00";
    /// Heights of the 14 waveform bars.
    [ObservableProperty] IReadOnlyList<double> _waveBars = LiveLayout.WaveShape.Select(s => LiveLayout.WaveBarHeight(s, 0)).ToList();

    void Tick()
    {
        var (elapsed, mic, sys) = _meter();
        ElapsedText = Exporter.Clock(elapsed);
        var level = Paused ? 0 : LevelMath.Meter(Math.Max(mic, sys));
        WaveBars = LiveLayout.WaveShape.Select(s => LiveLayout.WaveBarHeight(s, level)).ToList();
    }

    public bool Paused => _live.Paused;
    public string ListeningText => Paused ? "Paused" : "Listening";
    public string PauseTitle => Paused ? "Resume" : "Pause";
    public string Title => _live.Context?.Title ?? "";
    public string? TemplateTitle => _live.Context?.PlaybookTitle;
    public bool IsThinking => _live.Status is AssistStatus.Thinking;
    public bool IsWarning => _live.Status.IsWarning;
    public string StatusText => _live.Status.Text;
    public string UpdatesText => $"{State.Updates} updates";
    public string CostTooltip => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        "Each update uses some of your Claude subscription (API equivalent so far: ${0:0.00}).", State.CostUSD);

    void StatusChanged()
    {
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(IsThinking));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(QuestionEmptyText));
    }

    [RelayCommand] Task TogglePause() => _live.TogglePauseAsync();
    [RelayCommand] Task Stop() => _assist.StopAsync();

    // MARK: Catch-up (spec 03 §6.2)

    public bool ShowCatchUp => _live.CatchUp is not null || _live.CatchUpState is not StepState.Idle;
    public string CatchUpTitle => _live.CatchUpState.IsWorking ? "What did I miss?" : $"The last {_live.CatchUpMinutes} minutes";
    public string? CatchUpText => _live.CatchUp?.Text;
    public bool CatchUpBusy => _live.CatchUpState.IsWorking;

    void CatchUpChanged()
    {
        OnPropertyChanged(nameof(ShowCatchUp));
        OnPropertyChanged(nameof(CatchUpTitle));
        OnPropertyChanged(nameof(CatchUpText));
        OnPropertyChanged(nameof(CatchUpBusy));
        WhatDidIMissCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCatchUp))]
    Task WhatDidIMiss(string minutes) => _live.WhatDidIMissAsync(int.Parse(minutes, System.Globalization.CultureInfo.InvariantCulture));
    bool CanCatchUp(string? _) => !_live.CatchUpState.IsWorking;

    [RelayCommand] void CloseCatchUp() => _live.CloseCatchUp();

    // MARK: Next question (spec 03 §6.3)

    public QuestionItem? First => State.Questions.FirstOrDefault() is { } q ? new(q.Id, q.Text) : null;
    public bool HasQuestion => State.Questions.Count > 0;
    public bool FirstChanged => State.Questions.FirstOrDefault() is { } q && State.Changed.Contains(q.Id);
    public string? FirstChecklistItem => State.Questions.FirstOrDefault()?.PlaybookItem;
    public string? FirstWhy => State.Questions.FirstOrDefault()?.Why;
    public IReadOnlyList<QuestionItem> Alternatives => State.Questions.Skip(1).Select(q => new QuestionItem(q.Id, q.Text)).ToList();
    public string QuestionEmptyText => _live.Status is AssistStatus.Starting or AssistStatus.Thinking
        ? "Claude is reading the client context…" : "Waiting for the conversation…";

    [RelayCommand] Task SuggestNow() => _live.SuggestNowAsync();
    [RelayCommand] Task Other() => First is { } q ? _live.DismissAsync(q.Id) : Task.CompletedTask;
    [RelayCommand] Task Asked() => First is { } q ? _live.MarkAskedAsync(q.Id) : Task.CompletedTask;
    [RelayCommand] Task Promote(string id) => _live.PromoteAsync(id);

    // MARK: Panels (spec 03 §6.4)

    public IReadOnlyList<SummaryItem> Summary =>
        State.Summary.Select((p, i) => { var (t, b) = AssistUi.SummaryParagraph(p); return new SummaryItem(i, t, b, State.Changed.Contains($"s{i}")); }).ToList();
    public string SummaryCount => State.Summary.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    /// The highest changed paragraph, for scrolling it to the centre (null when none changed).
    public int? ScrollToParagraph => State.Changed.Where(c => c.StartsWith('s') && int.TryParse(c[1..], out _)).Select(c => int.Parse(c[1..])).DefaultIfEmpty(-1).Max() is var m && m >= 0 ? m : null;

    public IReadOnlyList<OpenPointItem> OpenPoints =>
        State.OpenPoints.OrderBy(p => p.Status == OpenPointStatus.Open ? 0 : 1)
            .Select(p => new OpenPointItem(p.Id, p.Text, p.Status, State.Changed.Contains($"o:{p.Id}"))).ToList();
    public string OpenCount => $"{State.OpenPoints.Count(p => p.Status == OpenPointStatus.Open)} open";

    [RelayCommand] Task ToggleOpenPoint(OpenPointItem p) => _live.SetOpenPointAsync(p.Id, AssistUi.Toggle(p.Status));

    public bool HasChecklist => State.Playbook.Count > 0;
    public IReadOnlyList<ChecklistItem> Checklist
    {
        get
        {
            var list = new List<ChecklistItem>();
            string? section = null;
            foreach (var p in State.Playbook)
            {
                string? header = null;
                if (p.Section != section) { section = p.Section; header = p.Section.Length > 0 ? p.Section : null; }
                list.Add(new ChecklistItem(p.Id, header, p.Text, p.Status, p.Evidence, State.Changed.Contains($"p:{p.Id}")));
            }
            return list;
        }
    }
    public string ChecklistCount => $"{State.Playbook.Count(p => p.Status == PlaybookStatus.Covered)}/{State.Playbook.Count}";
    public double ChecklistProgress => State.Playbook.Count == 0 ? 0 : State.Playbook.Count(p => p.Status == PlaybookStatus.Covered) / (double)State.Playbook.Count;

    [RelayCommand] Task ToggleChecklist(ChecklistItem p) => _live.SetPlaybookAsync(p.Id, AssistUi.Toggle(p.Status));

    // Width shares (stored per user) and transcript height.
    double _summaryShare, _openShare, _transcriptHeight;
    public (double Summary, double Open) Shares => LiveLayout.Shares(_summaryShare, _openShare, HasChecklist);

    /// Stores new shares after a drag.
    public void SetShares((double Summary, double Open) s)
    {
        _summaryShare = s.Summary; _openShare = s.Open;
        _ui.Set(UiKeys.SummaryShare, s.Summary);
        _ui.Set(UiKeys.OpenShare, s.Open);
        OnPropertyChanged(nameof(Shares));
    }

    public double StoredTranscriptHeight => _transcriptHeight;

    public void SetTranscriptHeight(double h)
    {
        _transcriptHeight = h;
        _ui.Set(UiKeys.TranscriptHeight, h);
        OnPropertyChanged(nameof(StoredTranscriptHeight));
    }

    // MARK: Tables (spec 03 §6.5)

    public bool HasTables => (State.Tables?.Count ?? 0) > 0;
    public IReadOnlyList<TableTile> Tables => (State.Tables ?? new()).Select(t => new TableTile(t.Id, AssistUi.TableName(t), $"{t.FilledCount}/{t.Rows.Count}",
        t.FilledCount / (double)Math.Max(t.Rows.Count, 1),
        AssistUi.LatestAnswers(t).Select(x => new TableTileLine(x.Text, State.Changed.Contains($"t:{x.RowId}"))).ToList())).ToList();

    /// The live table sheet's grid for one table (cell edits go to the engine).
    public TableGridViewModel TableGrid(string id) =>
        new(() => State.Tables?.FirstOrDefault(t => t.Id == id), () => State.Changed, (row, col, v) => _ = _live.SetTableCellAsync(row, col, v),
            onlyAnswered: false, subscribe: h => AssistUiHelpers.Observe(_live, p => { if (p == nameof(AssistLiveSession.State)) h(); }));

    // MARK: Attendees and transcript (spec 03 §6.6)

    public IReadOnlyList<AttendeeChip> PresentAttendees => Chips(AssistUi.SplitAttendees(State.Attendees).Present, false);
    public IReadOnlyList<AttendeeChip> MentionedAttendees => Chips(AssistUi.SplitAttendees(State.Attendees).Mentioned, true);
    public bool NoAttendees => State.Attendees.Count == 0;
    public bool HasMentioned => State.Attendees.Any(a => a.Present == false);

    IReadOnlyList<AttendeeChip> Chips(IEnumerable<Attendee> list, bool dim) => list.Select(a => new AttendeeChip(a, AssistUi.Initial(a.Name), a.Role,
        a.Speaker is { } s && s != a.Name ? $"= {s}" : null, a.ContactID is not null, a.Notes ?? "Click for details from Aya",
        State.Changed.Contains($"a:{a.Id}"), dim)).ToList();

    public IReadOnlyList<LiveLineItem> Lines => _live.LiveLines.Select(l =>
    {
        var name = _live.LiveName(l);
        var generic = name is not null && SpeakerNames.IsGeneric(name);
        return new LiveLineItem(l, name, l.Track == Track.Mic, generic, name is null ? "Speaker not recognised yet" : AssistUi.SpeakerTooltip(name));
    }).ToList();

    /// Person popover for an attendee chip (rename only when the attendee speaks under a live label).
    public PersonCardViewModel PersonFor(AttendeeChip chip)
    {
        var raw = _live.RawSpeaker(chip.Attendee);
        return new PersonCardViewModel(chip.Name, chip.Attendee.ContactID, chip.Attendee.Speaker, _live.PersonCard, _assist.Aya,
            () => _live.NameSuggestions, raw is null ? null : n => _ = _live.RenameSpeakerAsync(raw, n));
    }

    /// Person popover for a transcript speaker (with rename).
    public PersonCardViewModel? PersonFor(LiveLineItem line)
    {
        if (line.Line.Speaker is not { } raw || line.SpeakerName is not { } name) return null;
        var contact = State.Attendees.FirstOrDefault(a => a.Name == name)?.ContactID;
        string? label = State.SpeakerNames?.TryGetValue(raw, out var l) == true ? l : null;
        return new PersonCardViewModel(name, contact, label, _live.PersonCard, _assist.Aya, () => _live.NameSuggestions,
            n => _ = _live.RenameSpeakerAsync(raw, n));
    }

    // MARK: Floating window banner (spec 03 §6.8)

    /// The first question, or the status text.
    public string BannerText => State.Questions.FirstOrDefault()?.Text ?? StatusText;
    /// "N open point(s)", null when none are open.
    public string? BannerOpenText => State.OpenPoints.Count(p => p.Status == OpenPointStatus.Open) is > 0 and var n ? $"{n} open point{(n == 1 ? "" : "s")}" : null;

    void StateChanged()
    {
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(BannerOpenText));
        foreach (var n in new[]
                 {
                     nameof(UpdatesText), nameof(CostTooltip), nameof(First), nameof(HasQuestion), nameof(FirstChanged), nameof(FirstChecklistItem),
                     nameof(FirstWhy), nameof(Alternatives), nameof(Summary), nameof(SummaryCount), nameof(ScrollToParagraph), nameof(OpenPoints),
                     nameof(OpenCount), nameof(HasChecklist), nameof(Checklist), nameof(ChecklistCount), nameof(ChecklistProgress), nameof(Shares),
                     nameof(HasTables), nameof(Tables), nameof(PresentAttendees), nameof(MentionedAttendees), nameof(NoAttendees), nameof(HasMentioned),
                     nameof(Lines),
                 })
            OnPropertyChanged(n);
    }
}
