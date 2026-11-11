using CommunityToolkit.Mvvm.ComponentModel;
using ObservableObject = CommunityToolkit.Mvvm.ComponentModel.ObservableObject;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.App.ViewModels;

/// A past Assist meeting in the Meetings panel (spec 03 §9.1).
public sealed record MeetingRow(SessionInfo Info, bool IsSelected, double? Progress)
{
    public string Id => Info.Id;
    public string Title => Info.Title;
    public SessionStatus Status => Info.Status;
    public MeetingMode Mode => Info.Mode;
    public string DateLine => AssistUi.SessionLine(Info);
    public bool IsBusy => AssistController.IsBusy(Info);
    public bool HasProgress => Progress is not null && Info.Status == SessionStatus.Transcribing;
}

/// <summary>
/// The Meetings panel left of the Assist router (spec 03 §9.1): New Meeting, Import Recording, search and the
/// sessions that have an Assist record (not the one recording), newest first.
/// </summary>
public sealed partial class AssistMeetingsViewModel : ScreenViewModel
{
    readonly AssistController _assist;
    readonly IAssistHost _host;
    readonly Action<string> _showTranscript;
    readonly Action<string> _showInExplorer;
    readonly Action _import;

    public AssistMeetingsViewModel(AssistController assist, IAssistHost host, Action<string> showTranscript, Action<string> showInExplorer, Action import)
    {
        _assist = assist; _host = host; _showTranscript = showTranscript; _showInExplorer = showInExplorer; _import = import;
        AssistUiHelpers.Observe(assist, p =>
        {
            if (p is nameof(AssistController.Phase) or nameof(AssistController.Live)) { OnPropertyChanged(nameof(IsPrepare)); Refresh(); }
        });
        Refresh();
    }

    public override string ScreenName => "Meetings";

    [ObservableProperty] string _filter = "";
    partial void OnFilterChanged(string value) => Refresh();

    [ObservableProperty] IReadOnlyList<MeetingRow> _items = Array.Empty<MeetingRow>();
    public bool IsEmpty => Items.Count == 0;
    public string EmptyText => Filter.Trim().Length == 0 ? "Meetings with Assist appear here." : "Nothing matches.";
    /// The New Meeting row is highlighted while preparing.
    public bool IsPrepare => _assist.Phase is MeetingAssistant.Core.Assist.AssistPhase.Prepare;

    /// Re-reads the list (sessions or progress changed).
    public void Refresh()
    {
        var selected = _assist.ReviewSessionId;
        Items = _assist.Meetings(Filter).Select(s => new MeetingRow(s, s.Id == selected, _host.Progress(s.Id))).ToList();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    [RelayCommand] void NewMeeting() => _assist.FinishReview();
    [RelayCommand] void Import() => _import();

    [RelayCommand]
    void Open(MeetingRow row)
    {
        if (_assist.ReviewSessionId != row.Id) _assist.OpenReview(row.Id);
    }

    [RelayCommand] void ShowTranscript(MeetingRow row) => _showTranscript(row.Id);
    [RelayCommand] void ShowInExplorer(MeetingRow row) => _showInExplorer(row.Id);

    /// Delete… (disabled while busy): asks, then deletes (finishing the review when it is open).
    [RelayCommand(CanExecute = nameof(CanDelete))]
    void Delete(MeetingRow row)
    {
        if (AssistUiHelpers.ConfirmDestructive(AssistController.DeleteTitle(row.Title), AssistController.DeleteMessage, "Delete"))
        {
            _assist.DeleteMeeting(row.Id);
            Refresh();
        }
    }

    static bool CanDelete(MeetingRow? row) => row is not null && !row.IsBusy;
}
