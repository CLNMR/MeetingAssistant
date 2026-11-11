using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MeetingAssistant.App.Controls;
using ObservableObject = CommunityToolkit.Mvvm.ComponentModel.ObservableObject;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.App.ViewModels;

/// <summary>
/// Editor of one proposed value (recap field or CRM change): checkbox, current Aya content, the editor chosen by
/// field kind (Yes/No, select, Markdown, text) and the note or reason (spec 03 §8.2, §8.5).
/// </summary>
public sealed partial class ValueEditorViewModel : ObservableObject
{
    ProposedValue _v;
    readonly Action<Action<ProposedValue>> _change;

    /// <param name="change">Applies an edit to the underlying value (recap: through the review controller; CRM: in place).</param>
    public ValueEditorViewModel(ProposedValue v, Action<Action<ProposedValue>> change, bool showToggle = true, bool isRecap = true)
    {
        _v = v.Copy(); _change = change; ShowToggle = showToggle; IsRecap = isRecap;
    }

    public string Key => _v.Key;
    public string Label => _v.Label;
    /// CRM changes of new contacts have no per-field checkbox.
    public bool ShowToggle { get; }
    /// Recap fields show "Now" and the tag; CRM rows show "Now" and the reason.
    public bool IsRecap { get; }
    public bool ShowEditor => !ShowToggle || Include;

    public bool Include
    {
        get => _v.Include;
        set { if (_v.Include != value) _change(x => x.Include = value); }
    }

    public string Value
    {
        get => _v.Value;
        set { if (_v.Value != value) _change(x => x.Value = value); }
    }

    public bool HasOld => _v.Old.Length > 0;
    public string Old => _v.Old;
    /// 2 lines when ticked, 4 when not (recap); 3 lines for CRM rows.
    public int OldLines => !IsRecap ? 3 : Include ? 2 : 4;
    /// "Replaces the current text" (warning) when ticked, "Has content in Aya" when not.
    public string? Tag => IsRecap && HasOld ? (Include ? "Replaces the current text" : "Has content in Aya") : null;
    public bool TagIsWarning => Include;
    /// Recap: a note such as removed deadlines. CRM: the reason.
    public string? Note => _v.Reason;

    public bool IsBoolean => _v.Kind == AyaFieldKind.Boolean;
    public bool IsSelect => _v.Kind == AyaFieldKind.Select;
    public bool IsMarkdown => _v.Kind == AyaFieldKind.Markdown;
    public bool IsText => !IsBoolean && !IsSelect && !IsMarkdown;
    public IReadOnlyList<Segment> YesNo { get; } = new[] { new Segment("true", "Yes"), new Segment("false", "No") };
    public IReadOnlyList<Choice> Options => new[] { new Choice("", "—") }.Concat(_v.Options.Select(o => new Choice(o, o))).ToList();
    public object? SelectValue { get => _v.Value; set => Value = value as string ?? ""; }
    /// Yes/No editor: the value normalised ("true"/"false"; "yes", "ja", "1" count as true).
    public object? BoolValue { get => AssistUi.NormalizeBool(_v.Value); set { if (value is string s) Value = s; } }

    /// Takes a new snapshot of the value and raises what changed.
    public void Update(ProposedValue v)
    {
        var old = _v;
        _v = v.Copy();
        if (old.Include != v.Include)
        {
            OnPropertyChanged(nameof(Include)); OnPropertyChanged(nameof(ShowEditor)); OnPropertyChanged(nameof(OldLines));
            OnPropertyChanged(nameof(Tag)); OnPropertyChanged(nameof(TagIsWarning));
        }
        if (old.Value != v.Value) { OnPropertyChanged(nameof(Value)); OnPropertyChanged(nameof(SelectValue)); OnPropertyChanged(nameof(BoolValue)); }
        if (old.Old != v.Old) { OnPropertyChanged(nameof(Old)); OnPropertyChanged(nameof(HasOld)); OnPropertyChanged(nameof(Tag)); }
        if (old.Reason != v.Reason) OnPropertyChanged(nameof(Note));
        if (!old.Options.SequenceEqual(v.Options)) OnPropertyChanged(nameof(Options));
    }
}

/// A contact box of the CRM proposal: tick, name, "New contact" tag and its changes (spec 03 §8.5).
public sealed partial class ContactBoxViewModel : ObservableObject
{
    readonly ContactProposal _c;
    readonly Action _edited;

    public ContactBoxViewModel(ContactProposal c, Action edited)
    {
        _c = c; _edited = edited;
        Changes = c.Changes.Select(ch => CreateEditor(ch)).ToList();
    }

    ValueEditorViewModel CreateEditor(ProposedValue ch)
    {
        ValueEditorViewModel? vm = null;
        vm = new ValueEditorViewModel(ch, f => { f(ch); vm?.Update(ch.Copy()); _edited(); }, showToggle: _c.ContactID is not null, isRecap: false);
        return vm;
    }

    public string Name => _c.Name;
    public bool IsNew => _c.ContactID is null;
    public IReadOnlyList<ValueEditorViewModel> Changes { get; }

    public bool Include
    {
        get => _c.Include;
        set { if (_c.Include == value) return; _c.Include = value; OnPropertyChanged(); _edited(); }
    }
}

/// A table section of the review: header + grid (spec 03 §8.3).
public sealed record ReviewTable(string Id, TableGridViewModel Grid);

/// <summary>
/// Assist review screen for one session (spec 03 §8): speakers, the recap draft and its target meeting, the
/// template tables, the live notes and the CRM proposal. Logic is in <see cref="AssistReviewController"/>.
/// </summary>
public sealed partial class AssistReviewViewModel : ScreenViewModel
{
    readonly AssistController _assist;
    readonly IAssistHost _host;
    readonly Action<string> _showTranscript;
    readonly Dictionary<string, ValueEditorViewModel> _fields = new();
    List<ValueEditorViewModel> _fieldList = new();
    List<ReviewTable> _tables = new();
    List<ValueEditorViewModel> _clientChanges = new();
    List<ContactBoxViewModel> _contacts = new();
    CRMProposal? _crmShown;

    /// <param name="showTranscript">Switches to Scribe with this session selected.</param>
    public AssistReviewViewModel(AssistController assist, IAssistHost host, string sessionId, Action<string> showTranscript)
    {
        _assist = assist;
        _host = host;
        _showTranscript = showTranscript;
        SessionId = sessionId;
        AssistUiHelpers.Observe(Review, p =>
        {
            switch (p)
            {
                case nameof(AssistReviewController.Record):
                case nameof(AssistReviewController.Context):
                case nameof(AssistReviewController.State): RecordChanged(); break;
                case nameof(AssistReviewController.RecapFields): FieldsChanged(); break;
                case nameof(AssistReviewController.RecapState):
                    DraftCommand.NotifyCanExecuteChanged(); UploadCommand.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(SpeakersWaiting)); OnPropertyChanged(nameof(CanDelete)); DeleteCommand.NotifyCanExecuteChanged();
                    break;
                case nameof(AssistReviewController.Transcript): OnPropertyChanged(nameof(Speakers)); OnPropertyChanged(nameof(HasSpeakers)); break;
                case nameof(AssistReviewController.TargetMeetingID):
                    OnPropertyChanged(nameof(TargetID)); OnPropertyChanged(nameof(HasTarget)); OnPropertyChanged(nameof(UploadTitle)); break;
                case nameof(AssistReviewController.TargetChoices):
                case nameof(AssistReviewController.DayMeetings):
                case nameof(AssistReviewController.TargetRecord): OnPropertyChanged(nameof(TargetChoices)); OnPropertyChanged(nameof(TargetID)); break;
                case nameof(AssistReviewController.Tables): TablesChanged(); break;
                case nameof(AssistReviewController.TablesState): OnPropertyChanged(nameof(CompleteTitle)); OnPropertyChanged(nameof(CompleteIsAgent)); CompleteTablesCommand.NotifyCanExecuteChanged(); break;
                case nameof(AssistReviewController.Crm): CrmChanged(); break;
                case nameof(AssistReviewController.SelectedCount): OnPropertyChanged(nameof(ApplyTitle)); ApplyCommand.NotifyCanExecuteChanged(); break;
                case nameof(AssistReviewController.CrmState): ProposeCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); break;
            }
        });
        FieldsChanged();
        TablesChanged();
        CrmChanged();
    }

    public override string ScreenName => "Review";
    public string SessionId { get; }
    public AssistReviewController Review => _assist.Review;
    AssistConnection Aya => _assist.Aya;
    AssistContext? Context => Review.Context;

    void RecordChanged()
    {
        foreach (var n in new[] { nameof(Title), nameof(Subtitle), nameof(IsUploaded), nameof(HasMeetingState), nameof(Attendees), nameof(SummaryItems),
                                  nameof(OpenPointItems), nameof(ChecklistLine), nameof(HasClient), nameof(ClientTitle), nameof(CompleteTitle),
                                  nameof(CompleteIsAgent), nameof(TargetChoices) })
            OnPropertyChanged(n);
    }

    // MARK: Header

    public string Title => Context?.Title ?? "Meeting";
    public string Subtitle => string.Join(" · ", new[]
    {
        Context?.PlaybookTitle,
        Context is { } c ? c.Date.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture) : null,
    }.Where(s => !string.IsNullOrEmpty(s)));

    public bool CanDelete => !AssistController.IsBusy(_host.Session(SessionId));

    [RelayCommand(CanExecute = nameof(CanDelete))]
    void Delete()
    {
        var s = _host.Session(SessionId);
        if (s is null || AssistController.IsBusy(s)) return;
        if (AssistUiHelpers.ConfirmDestructive(AssistController.DeleteTitle(s.Title), AssistController.DeleteMessage, "Delete"))
            _assist.DeleteMeeting(SessionId);
    }

    [RelayCommand] void ShowTranscript() => _showTranscript(SessionId);

    /// Done: flush, reset, back to Prepare. Deliberately not the default button (Return never closes the review).
    [RelayCommand] void Done() => _assist.FinishReview();

    // MARK: Speakers (spec 03 §8.1)

    public IReadOnlyList<ReviewSpeakerRow> Speakers => Review.Transcript is { } t ? AssistUi.ReviewSpeakers(t) : new List<ReviewSpeakerRow>();
    public bool HasSpeakers => Review.Transcript is { Speakers.Count: > 0 };
    public string SpeakersWaiting => AssistUi.SpeakersWaiting(_host.Session(SessionId)?.Status, _host.Progress(SessionId));

    /// Person popover for a speaker of the final transcript (with rename).
    public PersonCardViewModel PersonFor(ReviewSpeakerRow row) =>
        new(row.Label, null, null, Review.PersonCard, Aya, () => Review.NameSuggestions, n => Review.RenameFinalSpeaker(row.Id, n));

    // MARK: Recap (spec 03 §8.2)

    public string? RecapTarget => Aya.Config.List(AyaSlot.Meetings) is { } r ? $"→ {r.Label} in {r.WorkspaceLabel}" : null;
    public bool IsUploaded => !string.IsNullOrEmpty(Review.Record?.RecapRecordID);

    [RelayCommand]
    void OpenInAya()
    {
        if (Review.UploadedUrl is { } u) App.Current.OpenUrl(u);
    }

    public bool HasDraft => Review.RecapFields.Count > 0;
    public string DraftTitle => HasDraft ? "Draft Again" : "Draft Recap";

    bool NotRecapWorking() => !Review.RecapState.IsWorking;

    /// Draft Recap, or Draft Again after confirming that edits are replaced.
    [RelayCommand(CanExecute = nameof(NotRecapWorking))]
    async Task Draft()
    {
        if (HasDraft && !AssistUiHelpers.ConfirmDestructive("Draft the recap again?",
                "Claude writes a new recap. Your edits to the current one are replaced.", "Draft Again")) return;
        await Review.DraftRecapAsync();
    }

    public IReadOnlyList<Choice> TargetChoices =>
        new[] { new Choice(null, "New meeting in Aya") }
            .Concat(Review.TargetChoices.Select(m => new Choice(m.Id,
                AssistUi.MeetingChoice(m, Aya.Config.MeetingDateField) + (m.Id == Context?.MeetingID ? " (planned)" : ""))))
            .ToList();

    public object? TargetID
    {
        get => Review.TargetMeetingID;
        set
        {
            var id = value as string;
            if (id != Review.TargetMeetingID) AssistUiHelpers.Fire(() => Review.SetTargetAsync(id));
        }
    }

    public bool HasTarget => Review.TargetMeetingID is not null;

    public IReadOnlyList<ValueEditorViewModel> Fields => _fieldList;

    void FieldsChanged()
    {
        var list = Review.RecapFields;
        if (list.Select(f => f.Key).SequenceEqual(_fieldList.Select(f => f.Key)))
        {
            foreach (var f in list) _fields[f.Key].Update(f);
        }
        else
        {
            _fields.Clear();
            _fieldList = list.Select(f =>
            {
                var key = f.Key;
                var vm = new ValueEditorViewModel(f, change => Review.UpdateField(key, change));
                _fields[key] = vm;
                return vm;
            }).ToList();
            OnPropertyChanged(nameof(Fields));
        }
        OnPropertyChanged(nameof(HasDraft));
        OnPropertyChanged(nameof(DraftTitle));
    }

    public string UploadTitle => Review.TargetMeetingID is null ? "Create Meeting in Aya" : "Save to Meeting";

    [RelayCommand(CanExecute = nameof(NotRecapWorking))]
    Task Upload() => Review.UploadRecapAsync();

    // MARK: Tables (spec 03 §8.3)

    public bool HasTables => _tables.Count > 0;
    public IReadOnlyList<ReviewTable> Tables => _tables;
    public string TablesSubtitle => Review.TablesFieldLabel is { } l
        ? $"Saved with the recap into “{l}”. Long tables only with the rows that were answered."
        : "Not saved to Aya (Settings › Assist › Template tables).";
    public string CompleteTitle => Review.Record?.Tables is null ? "Complete from Transcript" : "Complete Again";
    public bool CompleteIsAgent => Review.Record?.Tables is null;

    [RelayCommand(CanExecute = nameof(CanComplete))]
    Task CompleteTables() => Review.CompleteTablesAsync();
    bool CanComplete() => !Review.TablesState.IsWorking;

    void TablesChanged()
    {
        var ids = Review.Tables.Select(t => t.Id).ToList();
        if (ids.SequenceEqual(_tables.Select(t => t.Id)))
        {
            foreach (var t in _tables) t.Grid.Refresh();
        }
        else
        {
            _tables = Review.Tables.Select(t =>
            {
                var id = t.Id;
                return new ReviewTable(id, new TableGridViewModel(() => Review.Tables.FirstOrDefault(x => x.Id == id), () => new HashSet<string>(),
                    (row, col, v) => Review.SetCell(row, col, v), AssistUi.OnlyAnsweredByDefault(t)));
            }).ToList();
            OnPropertyChanged(nameof(Tables));
            OnPropertyChanged(nameof(HasTables));
        }
        OnPropertyChanged(nameof(TablesSubtitle));
        OnPropertyChanged(nameof(CompleteTitle));
        OnPropertyChanged(nameof(CompleteIsAgent));
    }

    // MARK: During the meeting (spec 03 §8.4)

    AssistState State => Review.State;
    public bool HasMeetingState => State.Summary.Count > 0 || State.OpenPoints.Count > 0 || State.Attendees.Count > 0 || State.Playbook.Count > 0;
    public IReadOnlyList<AttendeeChip> Attendees => State.Attendees.Select(a => new AttendeeChip(a, AssistUi.Initial(a.Name), a.Role, null,
        a.ContactID is not null, a.Notes ?? "Click for details from Aya", false, a.Present == false)).ToList();
    public IReadOnlyList<SummaryItem> SummaryItems =>
        State.Summary.Select((p, i) => { var (t, b) = AssistUi.SummaryParagraph(p); return new SummaryItem(i, t, b, false); }).ToList();
    public IReadOnlyList<OpenPointItem> OpenPointItems => State.OpenPoints.Select(p => new OpenPointItem(p.Id, p.Text, p.Status, false)).ToList();
    public string? ChecklistLine => Review.ChecklistLine;

    /// Person popover for an attendee (no rename in the review).
    public PersonCardViewModel PersonFor(AttendeeChip chip) =>
        new(chip.Name, chip.Attendee.ContactID, null, Review.PersonCard, Aya, () => Review.NameSuggestions, null);

    // MARK: CRM (spec 03 §8.5)

    public bool HasClient => Context?.ClientID is not null;
    public string ClientTitle => Context?.ClientTitle is { Length: > 0 } t ? t : "Client";
    public bool HasProposal => Review.Crm is not null;
    public string ProposeTitle => HasProposal ? "Propose Again" : "Propose Updates";
    public IReadOnlyList<ValueEditorViewModel> ClientChanges => _clientChanges;
    public bool HasClientChanges => _clientChanges.Count > 0;
    public IReadOnlyList<ContactBoxViewModel> Contacts => _contacts;
    public string ApplyTitle => $"Apply Selected ({Review.SelectedCount})";

    bool CanPropose() => !Review.CrmState.IsWorking;

    [RelayCommand(CanExecute = nameof(CanPropose))]
    async Task Propose()
    {
        if (HasProposal && !AssistUiHelpers.ConfirmDestructive("Propose the updates again?",
                "Claude makes a new proposal. Your ticks and edits in the current one are replaced.", "Propose Again")) return;
        await Review.ProposeCrmAsync();
    }

    bool CanApply() => Review.SelectedCount > 0 && !Review.CrmState.IsWorking;

    [RelayCommand(CanExecute = nameof(CanApply))]
    Task Apply() => Review.ApplyCrmAsync();

    void CrmChanged()
    {
        if (!ReferenceEquals(_crmShown, Review.Crm))
        {
            _crmShown = Review.Crm;
            var p = Review.Crm;
            _clientChanges = p?.Client.Select(c =>
            {
                ValueEditorViewModel? vm = null;
                vm = new ValueEditorViewModel(c, f => { f(c); vm?.Update(c.Copy()); Review.CrmEdited(); }, showToggle: true, isRecap: false);
                return vm;
            }).ToList() ?? new();
            _contacts = p?.Contacts.Select(c => new ContactBoxViewModel(c, Review.CrmEdited)).ToList() ?? new();
            OnPropertyChanged(nameof(ClientChanges)); OnPropertyChanged(nameof(HasClientChanges)); OnPropertyChanged(nameof(Contacts));
        }
        OnPropertyChanged(nameof(HasProposal)); OnPropertyChanged(nameof(ProposeTitle)); OnPropertyChanged(nameof(ApplyTitle));
        ApplyCommand.NotifyCanExecuteChanged();
    }
}
