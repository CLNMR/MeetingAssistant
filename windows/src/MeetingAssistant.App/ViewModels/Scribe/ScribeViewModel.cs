using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Services;
using MeetingAssistant.Core;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Scribe;
using MeetingAssistant.Core.Storage;
using WinShell = MeetingAssistant.Platform.Windows.SystemServices.Shell;

namespace MeetingAssistant.App.ViewModels;

/// One row of the recordings panel: title, status badge, mode icon, "d MMM, HH:mm · clock", progress.
public sealed partial class SessionRowViewModel : ObservableObject
{
    public SessionRowViewModel(SessionInfo info) => _info = info;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Id), nameof(Title), nameof(Status), nameof(Subtitle), nameof(ModeIcon), nameof(CanDelete))]
    SessionInfo _info;

    public string Id => Info.Id;
    public string Title => Info.Title;
    public SessionStatus Status => Info.Status;
    public string Subtitle => $"{ScribeRules.RowDate(Info.StartedAt)} · {Exporter.Clock(Info.DurationSec)}";
    /// Icon glyph of the mode (Icon.Call / Icon.InPerson / Icon.Hybrid).
    public string ModeIcon => ScribeIcons.Glyph(ScribeIcons.Mode(Info.Mode));
    /// Delete is disabled while recording, queued or transcribing.
    public bool CanDelete => !SessionEdit.IsBusy(Info.Status);

    /// Transcription progress 0…1 while processing; null otherwise.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(ProgressValue))]
    double? _progress;
    public bool HasProgress => Progress is not null;
    public double ProgressValue => Progress ?? 0;
}

/// Scribe mode: the recordings panel (290 px) and the main area, which shows the recorder or the
/// selected session (spec 01 "Scribe window, menus and shortcuts").
public sealed partial class ScribeViewModel : ObservableObject
{
    readonly ScribeController _scribe = ScribeController.Shared;
    readonly RecorderViewModel _recorder;
    readonly Dictionary<string, SessionRowViewModel> _rows = new();
    bool _syncing;

    /// All sessions, newest first.
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();

    /// Sessions shown in the list: not the one being recorded, filtered by title (case-insensitive).
    public ObservableCollection<SessionRowViewModel> Visible { get; } = new();

    /// Search text of the recordings panel.
    [ObservableProperty] string _filter = "";

    /// Selected session, or null for the recorder ("New Recording" row).
    [ObservableProperty] SessionRowViewModel? _selectedSession;

    /// The main area's view model: recorder or session detail.
    [ObservableProperty] ScreenViewModel _detail;

    /// "No recordings yet." or "Nothing matches." when the list is empty.
    public string EmptyText => ScribeRules.EmptyListText(Filter.Trim());
    public bool IsEmpty => Visible.Count == 0;

    /// Recording state for the "New Recording" row.
    public ScribeController Controller => _scribe;

    /// "New Recording" row while recording: "<clock> · <mode>".
    public string RecordingRowText => $"{Exporter.Clock(_scribe.Elapsed)} · {_scribe.RecordingMode.Title()}";

    public ScribeViewModel()
    {
        _recorder = new RecorderViewModel(_scribe);
        _detail = _recorder;
        _scribe.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ScribeController.Sessions) or nameof(ScribeController.RecordingSessionId)) Sync();
            if (e.PropertyName is nameof(ScribeController.Elapsed) or nameof(ScribeController.RecordingMode)) OnPropertyChanged(nameof(RecordingRowText));
        };
        _scribe.ProgressChanged += id =>
        {
            if (_rows.TryGetValue(id, out var row)) row.Progress = _scribe.ProgressOf(id)?.Fraction;
        };
        _scribe.SelectRequested += id => { if (id is null) SelectedSession = null; else Select(id); };
        Sync();
    }

    partial void OnFilterChanged(string value) => Refilter();

    partial void OnSelectedSessionChanged(SessionRowViewModel? oldValue, SessionRowViewModel? newValue)
    {
        if (_syncing) return;
        if (newValue is not null && Detail is SessionDetailViewModel d && d.SessionId == newValue.Id) return;
        (Detail as SessionDetailViewModel)?.Leave();
        Detail = newValue is null ? _recorder : new SessionDetailViewModel(_scribe, newValue.Info);
    }

    /// Selects the recorder ("New Recording" row).
    [RelayCommand] void ShowRecorder() => SelectedSession = null;

    /// Selects a session by id (launch argument `-select <id>`, after recording, import, split); unknown ids show the recorder.
    public void Select(string id)
    {
        if (!_rows.ContainsKey(id)) { _scribe.Reload(); Sync(); }
        SelectedSession = _rows.GetValueOrDefault(id);
    }

    /// Up/Down in the list.
    public void Move(int delta)
    {
        var i = ScribeRules.MoveSelection(Visible.Count, SelectedSession is null ? -1 : Visible.IndexOf(SelectedSession), delta);
        if (i >= 0) SelectedSession = Visible[i];
    }

    [RelayCommand] void TranscribeAgain(SessionRowViewModel? row) { if (row is not null) _scribe.Enqueue(row.Id); }

    [RelayCommand]
    void ShowInExplorer(SessionRowViewModel? row)
    {
        if (row is null) return;
        try { WinShell.ShowInExplorer(AppPaths.Session(row.Id)); } catch (Exception e) { _scribe.ReportError(e.Message); }
    }

    [RelayCommand]
    void Delete(SessionRowViewModel? row)
    {
        if (row is null || !row.CanDelete) return;
        var r = App.Current.ShowConfirm(ScribeRules.DeleteHeading(row.Title), ScribeRules.DeleteMessage(_scribe.HasAssistRecord(row.Id)),
            new AlertButton("Delete", AlertButtonKind.Destructive, IsDefault: true),
            new AlertButton("Cancel", IsCancel: true));
        if (r != 0) return;
        if (SelectedSession?.Id == row.Id) SelectedSession = null;
        _scribe.Delete(row.Id);
    }

    /// Updates the rows from the controller's sessions (keeps row objects, so selection and progress survive).
    void Sync()
    {
        var list = _scribe.Sessions;
        var ids = list.Select(s => s.Id).ToHashSet();
        foreach (var gone in _rows.Keys.Where(k => !ids.Contains(k)).ToList()) _rows.Remove(gone);
        Sessions.Clear();
        foreach (var s in list)
        {
            if (_rows.TryGetValue(s.Id, out var row)) row.Info = s;
            else _rows[s.Id] = row = new SessionRowViewModel(s);
            row.Progress = _scribe.ProgressOf(s.Id)?.Fraction;
            Sessions.Add(row);
        }
        Refilter();
        if (SelectedSession is { } sel)
        {
            if (!_rows.ContainsKey(sel.Id)) SelectedSession = null;
            else (Detail as SessionDetailViewModel)?.InfoChanged(sel.Info);
        }
    }

    void Refilter()
    {
        var keep = SelectedSession;
        var visible = ScribeRules.VisibleSessions(Sessions.Select(r => r.Info), _scribe.RecordingSessionId, Filter);
        _syncing = true;
        try
        {
            Visible.Clear();
            foreach (var s in visible) Visible.Add(_rows[s.Id]);
            // Clearing the list deselects in the ListBox; restore the selection without reloading the detail.
            if (SelectedSession != keep) SelectedSession = keep;
        }
        finally { _syncing = false; }
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// Icon keys and brushes used by the Scribe views.
public static class ScribeIcons
{
    /// Mode icon resource key.
    public static string Mode(MeetingMode m) => m switch
    {
        MeetingMode.InPerson => "Icon.InPerson",
        MeetingMode.Hybrid => "Icon.Hybrid",
        _ => "Icon.Call",
    };

    /// The glyph string of an icon resource key.
    public static string Glyph(string key) => System.Windows.Application.Current?.TryFindResource(key) as string ?? "";

    static readonly Dictionary<string, SolidColorBrush> Cache = new();

    /// Speaker colour (spec 01 "Speaker colours"): me = PrimaryBrush, others from the 10-colour palette.
    public static Brush Speaker(string id, bool isMe)
    {
        if (isMe) return (Brush)System.Windows.Application.Current.FindResource("PrimaryBrush");
        var hex = SpeakerPalette.Color(id, false)!;
        if (!Cache.TryGetValue(hex, out var b))
        {
            b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            Cache[hex] = b;
        }
        return b;
    }

    /// Brush from the theme (falls back to gray outside the app).
    public static Brush Theme(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
