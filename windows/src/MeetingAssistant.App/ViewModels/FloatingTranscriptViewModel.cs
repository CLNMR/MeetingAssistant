using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Infrastructure;
using MeetingAssistant.App.Services;
using MeetingAssistant.Core.Scribe;

namespace MeetingAssistant.App.ViewModels;

/// One paragraph of the floating transcript (spec 01 "Paragraphs"). <see cref="SpeakerBrush"/> is the
/// speaker colour (palette by "segment-speaker" key; muted for "…"); "me" uses the accent via <see cref="IsMe"/>.
public sealed record FloatingParagraph(string Speaker, string Time, string Final, string Pending, bool Recorded, bool IsMe,
                                       Brush? SpeakerBrush = null);

/// The floating live transcript window (spec 01 "Floating live transcript window"): font size,
/// controls and the paragraph list, fed by <see cref="ListenController"/> (attached at start-up).
public sealed partial class FloatingTranscriptViewModel : ObservableObject
{
    /// Font size limits and default (spec 01: 9–28 pt, default 13).
    public const double MinFont = 9, MaxFont = 28, DefaultFont = 13;
    /// Only the last 150 paragraphs are drawn.
    public const int ShownParagraphs = 150;

    readonly IUiState _ui;
    ListenController? _listen;

    public FloatingTranscriptViewModel(IUiState ui, ShellViewModel shell)
    {
        _ui = ui;
        Shell = shell;
        _fontSize = Math.Clamp(ui.Get(UiKeys.FloatingFontSize, DefaultFont), MinFont, MaxFont);
    }

    /// Recording state, Assist banner, "Open MeetingAssistant".
    public ShellViewModel Shell { get; }

    /// Paragraphs to draw (at most <see cref="ShownParagraphs"/>).
    public ObservableCollection<FloatingParagraph> Paragraphs { get; } = new();

    [ObservableProperty] double _fontSize;
    partial void OnFontSizeChanged(double value) => _ui.Set(UiKeys.FloatingFontSize, value);

    /// Listening paused by the user.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    bool _isPaused;

    /// Level dots lit (level > 0.0056, about −45 dB).
    [ObservableProperty] bool _micActive;
    [ObservableProperty] bool _speakerActive;
    /// The mode records call audio (second level dot visible).
    [ObservableProperty] bool _showSpeakerDot = true;

    /// Error text in orange in the transcript area (no microphone access, models missing).
    [ObservableProperty] string? _error;

    /// Following new lines; false after the user scrolled up ("jump to latest" visible).
    [ObservableProperty] bool _following = true;

    public string EmptyText => IsPaused && !Shell.IsRecording ? "Paused." : "Listening… nothing is saved unless you press record.";

    /// The "lit" threshold of the level dots.
    public const float LevelThreshold = 0.0056f;

    /// Raised for Copy Transcript and Clear (handled here once attached).
    public event Action? CopyRequested;
    public event Action? ClearRequested;
    public event Action<bool>? PauseChanged;

    [RelayCommand] void Larger() => FontSize = Math.Min(MaxFont, FontSize + 1);
    [RelayCommand] void Smaller() => FontSize = Math.Max(MinFont, FontSize - 1);
    [RelayCommand] void Copy() => CopyRequested?.Invoke();
    [RelayCommand] void Clear() => ClearRequested?.Invoke();
    [RelayCommand] void TogglePause() { IsPaused = !IsPaused; PauseChanged?.Invoke(IsPaused); }
    [RelayCommand] void JumpToLatest() => Following = true;

    /// Connects the window to always-on listening (App.StartScribe).
    public void Attach(ListenController listen, ScribeController scribe)
    {
        _listen = listen;
        listen.Changed += Refresh;
        listen.LevelsChanged += () =>
        {
            MicActive = listen.MicLevel > LevelThreshold;
            SpeakerActive = listen.SystemLevel > LevelThreshold;
        };
        CopyRequested += () =>
        {
            try { Clipboard.SetText(listen.PlainText); } catch (Exception e) { System.Diagnostics.Debug.WriteLine(e.Message); }
        };
        ClearRequested += listen.Clear;
        PauseChanged += paused => { if (paused) listen.StopListening(); else listen.StartListening(); };
        scribe.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScribeController.RecordingMode)) ShowSpeakerDot = listen.CapturesSystem;
            if (e.PropertyName == nameof(ScribeController.IsRecording)) OnPropertyChanged(nameof(EmptyText));
        };
        ShowSpeakerDot = listen.CapturesSystem;
        Refresh();
    }

    /// Window opened: start listening (unless the user paused it before).
    public void Opened()
    {
        IsPaused = false;
        _listen?.StartListening();
    }

    /// Window closed by the user (or the app quit): stop listening.
    public void Closed() => _listen?.StopListening();

    void Refresh()
    {
        if (_listen is null) return;
        Error = _listen.Error;
        var all = _listen.Paragraphs;
        var shown = all.Skip(Math.Max(0, all.Count - ShownParagraphs)).Select(ToRow).ToList();
        // Update in place so the list does not jump.
        for (int i = 0; i < shown.Count; i++)
        {
            if (i < Paragraphs.Count) { if (!Same(Paragraphs[i], shown[i])) Paragraphs[i] = shown[i]; }
            else Paragraphs.Add(shown[i]);
        }
        while (Paragraphs.Count > shown.Count) Paragraphs.RemoveAt(Paragraphs.Count - 1);
    }

    static bool Same(FloatingParagraph a, FloatingParagraph b) =>
        a.Speaker == b.Speaker && a.Time == b.Time && a.Final == b.Final && a.Pending == b.Pending && a.Recorded == b.Recorded
        && a.IsMe == b.IsMe && ReferenceEquals(a.SpeakerBrush, b.SpeakerBrush);

    static FloatingParagraph ToRow(PanelParagraph p) => new(
        p.Speaker, p.Date.ToString("HH:mm", CultureInfo.InvariantCulture),
        p.FinalText, (p.FinalText.Length > 0 && p.PendingText.Length > 0 ? " " : "") + p.PendingText,
        p.Recorded, p.IsMe,
        p.Speaker == "…" ? ScribeIcons.Theme("TextSubtleBrush") : ScribeIcons.Speaker(p.ColorKey, p.IsMe));
}
