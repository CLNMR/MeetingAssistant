using MeetingAssistant.Core.Aya;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.Core.Assist;

/// <summary>
/// The Import Recording sheet (spec 03 §9.2): one or two files, the date, a meeting in Aya on that day or a new
/// meeting with title, client, project and template; then <see cref="AssistController.ImportAsync"/>.
/// </summary>
public sealed class AssistImportModel : ObservableObject
{
    readonly AssistController _assist;
    readonly Func<DateTime, Task<List<AyaRecord>>> _loadDay;
    DateTime? _loadedDay;

    /// <param name="loadDay">For tests: replaces <see cref="AyaService.MeetingsOnDayOfAsync"/>.</param>
    public AssistImportModel(AssistController assist, Func<DateTime, Task<List<AyaRecord>>>? loadDay = null, DateTime? now = null)
    {
        _assist = assist;
        _loadDay = loadDay ?? (async d => assist.Aya.Service is { } svc ? await svc.MeetingsOnDayOfAsync(d) : new List<AyaRecord>());
        _date = now ?? DateTime.Now;
    }

    List<string> _files = new();
    DateTime _date;
    List<AyaRecord> _dayMeetings = new();
    string? _meetingID, _clientID, _projectID, _templateID;
    string _title = "";
    MeetingMode _twoTrackMode = MeetingMode.Call;
    StepState _state = StepState.IdleState;

    /// The chosen files (at most two: microphone, then call audio).
    public List<string> Files { get => _files; private set { if (Set(ref _files, value)) { Raise(nameof(HasTwoFiles)); Raise(nameof(FilesText)); Raise(nameof(ChooseTitle)); Raise(nameof(CanImport)); } } }
    /// Local date and time of the recording.
    public DateTime Date { get => _date; set { if (Set(ref _date, value)) _ = LoadDayAsync(); } }
    /// The Aya meetings on <see cref="Date"/>'s day.
    public List<AyaRecord> DayMeetings { get => _dayMeetings; private set => Set(ref _dayMeetings, value); }
    /// The meeting in Aya to save to, or null for a new one. Picking one sets the date to its time.
    public string? MeetingID
    {
        get => _meetingID;
        set
        {
            if (!Set(ref _meetingID, value)) return;
            Raise(nameof(IsNewMeeting));
            if (DayMeetings.FirstOrDefault(m => m.Id == value)?.Date(_assist.Aya.Config.MeetingDateField) is { } d) Date = d.ToLocalTime();
        }
    }
    public bool IsNewMeeting => MeetingID is null;
    public string Title { get => _title; set => Set(ref _title, value); }
    public string? ClientID { get => _clientID; set => Set(ref _clientID, value); }
    public string? ProjectID { get => _projectID; set => Set(ref _projectID, value); }
    public string? TemplateID { get => _templateID; set => Set(ref _templateID, value); }
    /// Mode for two files: Call or Hybrid (default Call).
    public MeetingMode TwoTrackMode { get => _twoTrackMode; set => Set(ref _twoTrackMode, value); }
    public StepState State { get => _state; private set { if (Set(ref _state, value)) Raise(nameof(CanImport)); } }

    public bool HasTwoFiles => Files.Count > 1;
    /// The file names joined by " + ".
    public string FilesText => string.Join(" + ", Files.Select(Path.GetFileName));
    /// "Choose…", or "Change…" once files are chosen.
    public string ChooseTitle => Files.Count == 0 ? "Choose…" : "Change…";
    /// Import needs files and nothing running.
    public bool CanImport => Files.Count > 0 && !State.IsWorking;
    /// The mode used: Call/Hybrid with two files, In person with one.
    public MeetingMode Mode => Files.Count > 1 ? TwoTrackMode : MeetingMode.InPerson;

    /// <summary>Files from the dialog: at most two are kept; the first file's creation date becomes the date.</summary>
    public void ChooseFiles(IEnumerable<string> paths, DateTime? firstCreated = null)
    {
        var list = paths.Take(AssistUi.MaxImportFiles).ToList();
        if (list.Count == 0) return;
        Files = list;
        if (firstCreated is { } d) Date = d;
    }

    /// <summary>Loads the day's meetings (when the day changed); a chosen meeting that is not on the new day is cleared.</summary>
    public async Task LoadDayAsync()
    {
        var day = Date.Date;
        if (_loadedDay == day) return;
        _loadedDay = day;
        List<AyaRecord> list;
        try { list = await _loadDay(Date); } catch (AyaException) { list = new(); }
        if (_loadedDay != day) return;
        DayMeetings = list;
        if (MeetingID is { } id && !list.Any(m => m.Id == id)) MeetingID = null;
    }

    /// <summary>Imports (spec 03 §9.2 "Import behaviour"). True when done (the sheet closes); on failure <see cref="State"/> shows the error.</summary>
    public async Task<bool> ImportAsync()
    {
        if (!CanImport) return false;
        State = new StepState.Working("Importing…");
        try
        {
            var isNew = MeetingID is null;
            await _assist.ImportAsync(Files, Mode, Date, isNew ? Title : "", MeetingID,
                                      isNew ? ClientID : null, isNew ? ProjectID : null, isNew ? TemplateID : null);
            State = StepState.IdleState;
            return true;
        }
        catch (Exception e)
        {
            State = new StepState.Failed(e.Message);
            return false;
        }
    }
}
