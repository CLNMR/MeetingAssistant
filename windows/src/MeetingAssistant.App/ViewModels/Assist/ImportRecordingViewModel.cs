using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using MeetingAssistant.App.Controls;
using ObservableObject = CommunityToolkit.Mvvm.ComponentModel.ObservableObject;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.Core.Assist;
using MeetingAssistant.Core.Model;
using Microsoft.Win32;

namespace MeetingAssistant.App.ViewModels;

/// <summary>
/// Import Recording sheet (spec 03 §9.2): files, date and time, mode for two files, a meeting in Aya on that day
/// or a new meeting (title, client, project, template). Logic in <see cref="AssistImportModel"/>.
/// </summary>
public sealed partial class ImportRecordingViewModel : ScreenViewModel
{
    readonly AssistImportModel _m;
    readonly AssistController _assist;

    public ImportRecordingViewModel(AssistController assist)
    {
        _assist = assist;
        _m = new AssistImportModel(assist);
        _timeText = _m.Date.ToString("HH:mm", CultureInfo.InvariantCulture);
        AssistUiHelpers.Observe(_m, p =>
        {
            OnPropertyChanged(p);
            switch (p)
            {
                case nameof(AssistImportModel.Files): OnPropertyChanged(nameof(Model)); break;
                case nameof(AssistImportModel.Date):
                    OnPropertyChanged(nameof(Day));
                    TimeText = _m.Date.ToString("HH:mm", CultureInfo.InvariantCulture);
                    break;
                case nameof(AssistImportModel.DayMeetings): OnPropertyChanged(nameof(MeetingChoices)); break;
                case nameof(AssistImportModel.MeetingID): OnPropertyChanged(nameof(MeetingID)); break;
                case nameof(AssistImportModel.State): ImportCommand.NotifyCanExecuteChanged(); break;
            }
            if (p is nameof(AssistImportModel.CanImport)) ImportCommand.NotifyCanExecuteChanged();
        });
        _ = _m.LoadDayAsync();
    }

    public override string ScreenName => "Import Recording";
    public AssistImportModel Model => _m;

    /// Raised when the import finished (the sheet closes).
    public event Action? Finished;

    // MARK: Files

    [RelayCommand]
    void Choose()
    {
        var d = new OpenFileDialog
        {
            Title = "Choose one recording, or two files: first your microphone, then the call audio.",
            Multiselect = true,
            Filter = "Audio and video|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma;*.caf;*.aiff;*.aif;*.mp4;*.mov;*.m4v;*.mkv;*.webm;*.avi;*.wmv|All files|*.*",
        };
        if (d.ShowDialog() != true || d.FileNames.Length == 0) return;
        DateTime? created = null;
        try { created = File.GetCreationTime(d.FileNames[0]); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _m.ChooseFiles(d.FileNames, created);
        TimeText = _m.Date.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    // MARK: Date and time

    /// The day (date picker).
    public DateTime? Day
    {
        get => _m.Date.Date;
        set { if (value is { } d) _m.Date = d.Date + _m.Date.TimeOfDay; }
    }

    /// The time as "HH:mm"; applied when it parses.
    [ObservableProperty] string _timeText;

    partial void OnTimeTextChanged(string value)
    {
        if (TimeSpan.TryParseExact(value.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1)
            && _m.Date.TimeOfDay != t)
            _m.Date = _m.Date.Date + t;
    }

    // MARK: Mode and meeting

    public IReadOnlyList<Segment> TwoTrackModes { get; } = new[] { new Segment(MeetingMode.Call, MeetingMode.Call.Title()), new Segment(MeetingMode.Hybrid, MeetingMode.Hybrid.Title()) };
    public object TwoTrackMode { get => _m.TwoTrackMode; set { if (value is MeetingMode m) { _m.TwoTrackMode = m; OnPropertyChanged(nameof(ModeExplanation)); } } }
    public string ModeExplanation => _m.TwoTrackMode.Explanation();

    public IReadOnlyList<Choice> MeetingChoices =>
        new[] { new Choice(null, "New meeting in Aya") }
            .Concat(_m.DayMeetings.Select(m => new Choice(m.Id, AssistUi.MeetingChoice(m, _assist.Aya.Config.MeetingDateField)))).ToList();
    public object? MeetingID { get => _m.MeetingID; set => _m.MeetingID = value as string; }

    public IReadOnlyList<Choice> ClientChoices => None(_assist.Aya.Clients.Select(r => new Choice(r.Id, r.Title)));
    public IReadOnlyList<Choice> ProjectChoices => None(_assist.Aya.Projects.Select(r => new Choice(r.Id, r.Title)));
    public IReadOnlyList<Choice> TemplateChoices => None(_assist.Aya.Templates.Select(r => new Choice(r.Id, r.Title)));
    static IReadOnlyList<Choice> None(IEnumerable<Choice> list) => new[] { new Choice(null, "None") }.Concat(list).ToList();

    public object? ClientID { get => _m.ClientID; set => _m.ClientID = value as string; }
    public object? ProjectID { get => _m.ProjectID; set => _m.ProjectID = value as string; }
    public object? TemplateID { get => _m.TemplateID; set => _m.TemplateID = value as string; }

    // MARK: Import

    [RelayCommand(CanExecute = nameof(CanImport))]
    async Task Import()
    {
        if (await _m.ImportAsync()) Finished?.Invoke();
    }

    bool CanImport() => _m.CanImport;
}
