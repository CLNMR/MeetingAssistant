using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Services;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;
using MeetingAssistant.Core.Scribe;
using MeetingAssistant.Core.Storage;
using MeetingAssistant.Platform.Windows.Audio;

namespace MeetingAssistant.App.ViewModels;

/// A speaker chip: colour dot, label, talk share; its menu renames, marks me and merges.
public sealed record SpeakerChip(string Id, string Label, string Share, Brush Brush, bool IsMe, IReadOnlyList<MenuChoice> MergeTargets);

/// A menu entry with a command (merge targets, "Change speaker to", Transcribe Again languages).
public sealed record MenuChoice(string Title, IRelayCommand Command, bool IsChecked = false);

/// A transcript line.
public sealed partial class TurnRowViewModel(Turn turn, string label, Brush brush) : ObservableObject
{
    public Turn Turn { get; } = turn;
    public int Id => Turn.Id;
    public string Time => Exporter.Clock(Turn.Start);
    public string Label { get; } = label;
    public Brush LabelBrush { get; } = brush;
    public string Text => Turn.Text;
    /// The line being played.
    [ObservableProperty] bool _isCurrent;
}

/// A pause marker between transcript lines (spec 01 "Pauses and splitting at pauses").
public sealed record PauseRowViewModel(Pause Pause)
{
    public string Time => Exporter.Clock(Pause.Start);
    public string Text => ScribeRules.PauseText(Pause);
    public string Range => $"{Exporter.Clock(Pause.Start)} – {Exporter.Clock(Pause.End)}";
    public string ActionTitle => ScribeRules.PauseAction(Pause);
    /// Amber when it suggests a split or is at the start/end; muted otherwise.
    public bool IsStrong => Pause.SuggestsSplit || Pause.Kind != PauseKind.Inner;
}

/// Scribe session detail (spec 01 "Session detail", "Speakers", "Pauses", "Playback", "Exports").
public sealed partial class SessionDetailViewModel : ScreenViewModel
{
    readonly ScribeController _scribe;
    readonly AppServices _services = AppServices.Shared;
    SessionPlayer? _player;
    readonly DispatcherTimer _poll;
    List<Pause> _pauses = new();

    public override string ScreenName => "Session detail";

    public SessionDetailViewModel(ScribeController scribe, SessionInfo info)
    {
        _scribe = scribe;
        _info = info;
        _title = info.Title;
        _transcript = SessionStore.LoadTranscript(info.Id);
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(0.2), DispatcherPriority.Background, (_, _) => Poll(), Application.Current.Dispatcher);
        _scribe.ProgressChanged += OnProgressChanged;
        Rebuild();
    }

    public string SessionId => Info.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeTitle), nameof(ModeIcon), nameof(DateText), nameof(PositionText), nameof(EmptyText),
                              nameof(IsFailed), nameof(HasAssist), nameof(CanEdit))]
    SessionInfo _info;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranscript), nameof(SpeakersText), nameof(ShowEmpty), nameof(ShowTranscript))]
    Transcript? _transcript;

    /// Editable title (saved with Enter or when the field loses focus).
    [ObservableProperty] string _title;

    /// Search text (filters turns by text or speaker label).
    [ObservableProperty] string _search = "";
    partial void OnSearchChanged(string value) => RebuildRows();

    // ---- Header ----

    public string ModeTitle => Info.Mode.Title();
    public string ModeIcon => ScribeIcons.Glyph(ScribeIcons.Mode(Info.Mode));
    public string DateText => ScribeRules.HeaderDate(Info.StartedAt);
    public string? SpeakersText => Transcript is { } t ? ScribeRules.SpeakersText(t.Speakers.Count) : null;
    public string PositionText => $"· {Exporter.Clock(CurrentTime)} / {Exporter.Clock(Info.DurationSec)}";
    public bool HasAssist => SessionStore.HasAssistRecord(Info.Id);
    public bool AssistRunning => App.Current.Shell?.Assist.IsRunning == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    double _currentTime;

    [ObservableProperty] bool _isPlaying;

    /// The title field was edited: rename (empty titles are ignored).
    [RelayCommand]
    public void CommitTitle()
    {
        var t = Title.Trim();
        if (t.Length == 0 || t == Info.Title) { Title = Info.Title; return; }
        _scribe.Rename(Info.Id, t);
    }

    /// Opens Assist's review of this session (the Assist area subscribes).
    [RelayCommand] void AssistRecap() => ScribeController.RequestAssistReview(Info.Id);

    /// Called by the list when the session's info changed on disk (status, title, duration).
    public void InfoChanged(SessionInfo info)
    {
        var statusChanged = info.Status != Info.Status;
        var durationChanged = info.DurationSec != Info.DurationSec;
        Info = info;
        if (Title.Trim() == Info.Title || !IsTitleFocused) Title = info.Title;
        if (statusChanged || durationChanged)
        {
            Transcript = SessionStore.LoadTranscript(info.Id);
            if (durationChanged) { _player?.Dispose(); _player = null; }
            Rebuild();
        }
        OnPropertyChanged(nameof(IsProcessing));
        RefreshCommands();
    }

    /// Set by the view while the title field has focus.
    public bool IsTitleFocused { get; set; }

    /// Leaving the session pauses playback.
    public void Leave()
    {
        _poll.Stop();
        _player?.Dispose();
        _player = null;
        IsPlaying = false;
        _scribe.ProgressChanged -= OnProgressChanged;
    }

    // ---- Processing ----

    void OnProgressChanged(string id)
    {
        if (id != Info.Id) return;
        OnPropertyChanged(nameof(IsProcessing)); OnPropertyChanged(nameof(ProgressFraction)); OnPropertyChanged(nameof(ProgressMessage));
        OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(ShowTranscript));
        RefreshCommands();
    }

    public bool IsProcessing => _scribe.ProgressOf(Info.Id) is not null;
    public double ProgressFraction => _scribe.ProgressOf(Info.Id)?.Fraction ?? 0;
    public string ProgressMessage => _scribe.ProgressOf(Info.Id)?.Message ?? "";
    public bool HasTranscript => Transcript is not null;
    public bool ShowTranscript => !IsProcessing && HasTranscript;
    public bool ShowEmpty => !IsProcessing && !HasTranscript;
    public bool IsFailed => Info.Status == SessionStatus.Failed;
    public string EmptyText => ScribeRules.EmptyStateText(Info.Status);
    /// Edit and Transcribe Again are disabled while processing.
    public bool CanEdit => !IsProcessing && !SessionEdit.IsBusy(Info.Status);

    [RelayCommand] void Cancel() => _scribe.CancelProcessing();
    [RelayCommand] void TranscribeNow() => _scribe.Enqueue(Info.Id);

    void RefreshCommands() => OnPropertyChanged(nameof(CanEdit));

    // ---- Playback ----

    SessionPlayer Player => _player ??= CreatePlayer();

    SessionPlayer CreatePlayer()
    {
        var p = new SessionPlayer(Info.Id, Info.Tracks);
        p.Finished += () => AppServices.Ui(Poll);
        return p;
    }

    /// Play/pause (Space).
    [RelayCommand]
    public void TogglePlay()
    {
        try { Player.Toggle(); }
        catch (Exception e) { _scribe.ReportError($"Could not play: {e.Message}"); }
        Poll();
        if (IsPlaying) _poll.Start();
    }

    /// Plays from a time (seeks when already playing).
    public void PlayFrom(double t)
    {
        try
        {
            if (Player.IsPlaying) Player.Seek(t); else Player.Play(t);
        }
        catch (Exception e) { _scribe.ReportError($"Could not play: {e.Message}"); }
        Poll();
        _poll.Start();
    }

    /// Jumps (timeline click/drag); keeps playing if it was.
    public void Seek(double t)
    {
        Player.Seek(Math.Clamp(t, 0, Info.DurationSec));
        Poll();
    }

    /// Pauses (before an edit sheet).
    public void PausePlayback()
    {
        _player?.Pause();
        Poll();
    }

    [RelayCommand] void PlayTurn(TurnRowViewModel? row) { if (row is not null) PlayFrom(row.Turn.Start); }
    [RelayCommand] void PlayPause(PauseRowViewModel? row) { if (row is not null) PlayFrom(row.Pause.Start); }

    /// Raised when the current line changed while playing (the view keeps it centred).
    public event Action<TurnRowViewModel>? CurrentRowChanged;

    void Poll()
    {
        // (The timer starts with its constructor: stop it while nothing plays.)
        if (_player is null) { IsPlaying = false; _poll.Stop(); return; }
        IsPlaying = _player.IsPlaying;
        CurrentTime = _player.CurrentTime;
        if (!IsPlaying) _poll.Stop();
        var current = Transcript is { } t ? ScribeRules.CurrentTurn(t.Turns, CurrentTime) : null;
        foreach (var row in Rows.OfType<TurnRowViewModel>())
        {
            var on = IsPlaying && row.Id == current;
            if (row.IsCurrent != on)
            {
                row.IsCurrent = on;
                if (on && Search.Trim().Length == 0) CurrentRowChanged?.Invoke(row);
            }
        }
    }

    // ---- Timeline and pauses ----

    /// Speech blocks of the timeline.
    public IReadOnlyList<(double Start, double End)> Blocks { get; private set; } = Array.Empty<(double, double)>();
    /// Pauses (timeline and rows).
    public IReadOnlyList<Pause> PauseList => _pauses;

    /// Split hint banner (longest split-suggesting pause, unless dismissed).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SplitHintText), nameof(HasSplitHint))]
    Pause? _splitHint;
    public bool HasSplitHint => SplitHint is not null;
    public string SplitHintText => SplitHint is { } p ? ScribeRules.SplitHintText(p) : "";

    [RelayCommand] void HintPlay() { if (SplitHint is { } p) PlayFrom(p.End - Pauses.Margin); }

    [RelayCommand]
    void HintDismiss()
    {
        _services.Settings.DismissSplitHint(Info.Id);
        SplitHint = null;
    }

    [RelayCommand] void HintSplit() { if (SplitHint is { } p) ActOn(p); }

    [RelayCommand] void PauseAction(PauseRowViewModel? row) { if (row is not null) ActOn(row.Pause); }

    /// The pause's own action: split an inner pause, trim a leading or trailing one.
    void ActOn(Pause p)
    {
        switch (p.Kind)
        {
            case PauseKind.Inner: OpenSplit(p); break;
            case PauseKind.Leading: OpenTrim(Math.Max(0, p.End - Pauses.Margin), null); break;
            default: OpenTrim(null, p.KeepUntil); break;
        }
    }

    /// Raised when the timeline must redraw (blocks, pauses, playhead).
    public event Action? TimelineChanged;

    partial void OnCurrentTimeChanged(double value) => TimelineChanged?.Invoke();

    // ---- Speakers and rows ----

    public ObservableCollection<SpeakerChip> Speakers { get; } = new();
    public ObservableCollection<object> Rows { get; } = new();

    void Rebuild()
    {
        var t = Transcript;
        _pauses = t is null ? new() : Pauses.Find(t, Info.DurationSec);
        Blocks = t is null ? Array.Empty<(double, double)>() : ScribeRules.TimelineBlocks(t.Turns, Info.DurationSec);
        SplitHint = t is null ? null : ScribeRules.SplitHint(_pauses, Info.Id, _services.Settings.DismissedSplitHints);
        Speakers.Clear();
        if (t is not null)
        {
            foreach (var s in t.Speakers)
            {
                var targets = t.Speakers.Where(o => o.Id != s.Id)
                    .Select(o => new MenuChoice(o.Label, new RelayCommand(() => Edit(tr => tr.Merge(s.Id, o.Id))))).ToList();
                Speakers.Add(new SpeakerChip(s.Id, s.Label, ScribeRules.TalkShare(t, s.Id), ScribeIcons.Speaker(s.Id, s.IsMe), s.IsMe, targets));
            }
        }
        OnPropertyChanged(nameof(SpeakersText));
        RebuildRows();
        TimelineChanged?.Invoke();
    }

    void RebuildRows()
    {
        Rows.Clear();
        if (Transcript is not { } t) return;
        var turns = ScribeRules.FilterTurns(t, Search);
        foreach (var r in ScribeRules.Rows(turns, _pauses, Search.Trim().Length > 0))
        {
            if (r is Turn turn)
            {
                var s = t.Speakers.FirstOrDefault(x => x.Id == turn.Speaker);
                Rows.Add(new TurnRowViewModel(turn, s?.Label ?? turn.Speaker, ScribeIcons.Speaker(turn.Speaker, s?.IsMe ?? false)));
            }
            else if (r is Pause p) Rows.Add(new PauseRowViewModel(p));
        }
        Poll();
    }

    /// Applies an edit, saves `transcript.json` immediately and redraws.
    void Edit(Action<Transcript> change)
    {
        if (Transcript is null) return;
        var t = Transcript.Clone();
        change(t);
        try { SessionStore.SaveTranscript(t, Info.Id); }
        catch (Exception e) { _scribe.ReportError($"Could not save: {e.Message}"); return; }
        Transcript = t;
        Rebuild();
    }

    /// Rename… (alert with name field). Learns the voice unless it is "me".
    [RelayCommand]
    void RenameSpeaker(SpeakerChip? chip)
    {
        if (chip is null || Transcript is null) return;
        var name = Views.PromptWindow.Ask("Rename speaker",
            "MeetingAssistant remembers this voice and uses the name automatically in future meetings.",
            ScribeRules.RenamePrefill(chip.Label), "Name");
        if (name is null || name.Trim().Length == 0) return;
        var n = name.Trim();
        var speaker = Transcript.Speakers.FirstOrDefault(s => s.Id == chip.Id);
        Edit(t => { if (t.Speakers.FirstOrDefault(s => s.Id == chip.Id) is { } s) s.Label = n; });
        if (speaker?.Embedding is { Length: > 0 } e && !speaker.IsMe)
        {
            try { _scribe.LearnSpeaker(n, e); }
            catch (Exception ex) { _scribe.ReportError($"Could not save the voice: {ex.Message}"); }
        }
    }

    /// "This is me".
    [RelayCommand]
    void MarkMe(SpeakerChip? chip)
    {
        if (chip is null || chip.IsMe) return;
        Edit(t => t.MarkMe(chip.Id, _services.Preferences.MyName));
    }

    /// The "Change speaker to" entries of a line: every speaker, then "New speaker".
    public IReadOnlyList<MenuChoice> ReassignChoices(TurnRowViewModel row)
    {
        if (Transcript is not { } t) return Array.Empty<MenuChoice>();
        var list = t.Speakers.Select(s => new MenuChoice(s.Label, new RelayCommand(() => Edit(tr => tr.Reassign(row.Id, s.Id))))).ToList();
        list.Add(new MenuChoice("New speaker", new RelayCommand(() => Edit(tr =>
        {
            var id = ScribeRules.NewManualSpeakerId();
            tr.Speakers.Add(new Speaker { Id = id, Label = tr.UnusedSpeakerLabel(), Track = row.Turn.Track, Embedding = null, IsMe = false });
            tr.Reassign(row.Id, id);
        }))));
        return list;
    }

    /// Line "Copy": `<label>: <text>`.
    [RelayCommand] void CopyTurn(TurnRowViewModel? row) { if (row is not null) Copy($"{row.Label}: {row.Text}"); }

    // ---- Export ----

    [RelayCommand] void CopyMarkdown() => Export(ExportFormat.Markdown, true);
    [RelayCommand] void CopyText() => Export(ExportFormat.Text, true);
    [RelayCommand] void SaveAs(ExportFormat format) => Export(format, false);

    void Export(ExportFormat f, bool toClipboard)
    {
        if (Transcript is not { } t) return;
        string text;
        try { text = Exporter.Render(t, Info, f); }
        catch (Exception e) { _scribe.ReportError($"Could not export: {e.Message}"); return; }
        if (toClipboard) { Copy(text); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = SafeFileName(Exporter.DefaultFileName(Info, f)),
            DefaultExt = "." + f.Extension(),
            Filter = $"{f.Title()} (*.{f.Extension()})|*.{f.Extension()}",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, text, new System.Text.UTF8Encoding(false)); }
        catch (Exception e) { _scribe.ReportError($"Could not save: {e.Message}"); }
    }

    static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));

    static void Copy(string s)
    {
        try { Clipboard.SetText(s); } catch (Exception e) { System.Diagnostics.Debug.WriteLine($"Clipboard failed: {e.Message}"); }
    }

    // ---- Transcribe Again (Windows: Parakeet only) ----

    /// Languages under "Parakeet Ultra (fast)"; the check marks the session's language when the current
    /// transcript (or the preference) uses Parakeet. Qwen3 is not offered on Windows.
    public IReadOnlyList<MenuChoice> RetranscribeChoices
    {
        get
        {
            var current = AsrEngineText.ParseLanguage(Info.Language) ?? TranscriptLanguage.Auto;
            var engine = Transcript?.Engine is { Length: > 0 } e ? e : _services.Preferences.Engine;
            return Enum.GetValues<TranscriptLanguage>().Select(l => new MenuChoice(l.Title(),
                new RelayCommand(() => _scribe.Retranscribe(Info.Id, AsrEngine.ParakeetUltra, l)),
                l == current && engine == AsrEngine.ParakeetUltra.Raw())).ToList();
        }
    }

    // ---- Trim / split ----

    [RelayCommand] void Trim() => OpenTrim(null, null);
    [RelayCommand] void Split() => OpenSplit(null);

    void OpenTrim(double? start, double? end)
    {
        if (!CanEdit) return;
        PausePlayback();
        var vm = new TrimSheetViewModel(Info, CurrentTime, ScribeRules.TrimLastSpeech(Transcript, Info.DurationSec), start, end);
        if (Views.SheetHost.Show(vm) && vm.Confirmed) _scribe.Trim(Info.Id, vm.Start.Value, vm.End.Value);
    }

    void OpenSplit(Pause? pause)
    {
        if (!CanEdit) return;
        PausePlayback();
        var vm = new SplitSheetViewModel(Info, CurrentTime, pause);
        if (!(Views.SheetHost.Show(vm) && vm.Confirmed)) return;
        if (vm.AtPause is { } p) _scribe.Split(Info.Id, p, vm.RemovePause);
        else _scribe.Split(Info.Id, vm.At.Value);
    }
}
