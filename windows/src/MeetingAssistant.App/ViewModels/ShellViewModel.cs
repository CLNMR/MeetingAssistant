using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Infrastructure;
using MeetingAssistant.Core.Model;

namespace MeetingAssistant.App.ViewModels;

/// The app's two modes (spec 03 §1). Stored as "scribe" / "assist" under `appMode`.
public enum AppMode { Scribe, Assist }

/// Opens and closes the app's windows; implemented by <see cref="App"/>.
public interface IWindowService
{
    void ShowMain();
    /// Opens Settings, optionally on a tab ("General", "Dictation", "Vocabulary", "Voices", "Models", "Assist").
    void ShowSettings(string? tab = null);
    void ToggleFloating();
    bool IsFloatingOpen { get; }
    /// Modal alert in the Aya style; returns the index of the clicked button (Cancel/Escape = -1).
    int ShowAlert(string title, string message, params AlertButton[] buttons);
    void OpenUrl(string url);
    void Quit();
}

/// One button of an alert (spec 03 §1 "Global error alert", 01 delete confirmation).
public sealed record AlertButton(string Text, AlertButtonKind Kind = AlertButtonKind.Secondary, bool IsDefault = false, bool IsCancel = false);
public enum AlertButtonKind { Primary, Secondary, Destructive }

/// Root view model of the main window, the tray menu and the global shortcuts: mode switch, rail,
/// recording state as far as the shell shows it, and the commands of the File/View menus
/// (spec 03 §1, 01 "Scribe window, menus and shortcuts", 04 §6.7).
public sealed partial class ShellViewModel : ObservableObject
{
    readonly IUiState _ui;
    readonly IWindowService _windows;

    public ShellViewModel(IUiState ui, IWindowService windows)
    {
        _ui = ui;
        _windows = windows;
        _mode = ui.Get(UiKeys.AppMode, "scribe") == "assist" ? AppMode.Assist : AppMode.Scribe;
        _myName = ui.Get(UiKeys.MyName, "Me");
        _dictationEnabled = ui.Get(UiKeys.DictationEnabled, true);
        Assist.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AssistViewModel.IsRunning)) { OnPropertyChanged(nameof(AssistDot)); }
        };
        ui.Changed += key =>
        {
            if (key == UiKeys.MyName) MyName = ui.Get(UiKeys.MyName, "Me");
            if (key == UiKeys.DictationEnabled) DictationEnabled = ui.Get(UiKeys.DictationEnabled, true);
        };
    }

    public ScribeViewModel Scribe { get; } = new();
    public AssistViewModel Assist { get; } = new();

    /// Selected mode; persisted under `appMode`.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(IsScribe), nameof(IsAssist), nameof(ScribeDot), nameof(AssistDot))]
    AppMode _mode;

    partial void OnModeChanged(AppMode value) => _ui.Set(UiKeys.AppMode, value == AppMode.Assist ? "assist" : "scribe");

    /// The mode's content (ScribeViewModel or AssistViewModel), shown through DataTemplates.
    public object Current => Mode == AppMode.Scribe ? Scribe : Assist;
    public bool IsScribe => Mode == AppMode.Scribe;
    public bool IsAssist => Mode == AppMode.Assist;

    /// "My name" preference (default "Me").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initials))]
    string _myName;

    /// Rail initials: first letters of the first two words of my name, uppercased; "ME" when empty.
    public string Initials => MakeInitials(MyName);

    /// Spec 03 §1: first letters of the first two words, or "ME".
    public static string MakeInitials(string? name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2);
        var s = string.Concat(parts.Select(p => p[0])).ToUpperInvariant();
        return s.Length == 0 ? "ME" : s;
    }

    /// A recording runs (set by the recording controller in the next phase).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScribeDot), nameof(RecordingMenuTitle), nameof(ToggleRecordingTitle))]
    bool _isRecording;

    /// Elapsed recording time, already formatted as the Mac clock (e.g. "12:04").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordingMenuTitle))]
    string _recordingClock = "0:00";

    /// Mode of the running (or next) recording.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordingMenuTitle))]
    MeetingMode _recordingMode = MeetingMode.Call;

    /// Number of recordings being transcribed (tray "Transcribing N recording(s)…").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranscribingText))]
    int _transcribingCount;

    /// Dictation enabled (tray shows the dictation entries).
    [ObservableProperty] bool _dictationEnabled;

    /// Tray info lines "Dictation: hold <shortcut>" / "Scribe+: hold <shortcut>" (null hides them).
    [ObservableProperty] string? _dictationHotKeyText;
    [ObservableProperty] string? _scribePlusHotKeyText;
    /// Tray "Copy Last Dictation" visible.
    [ObservableProperty] bool _hasLastDictation;

    /// Aya link of the rail logo (meetings list or organization); null when Aya is not connected.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogoTooltip))]
    string? _ayaUrl;
    /// True when <see cref="AyaUrl"/> points at the meetings list (tooltip text).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogoTooltip))]
    bool _ayaUrlIsMeetings;

    public string LogoTooltip => AyaUrl is null
        ? "Aya is not connected yet. Connect it in Settings › Assist."
        : AyaUrlIsMeetings ? "Open the meetings in Aya" : "Open Aya";

    /// Red dot on the Scribe button: recording while another mode is shown.
    public bool ScribeDot => IsRecording && Mode != AppMode.Scribe;
    /// Red dot on the Assist button: an Assist meeting runs while Scribe is shown.
    public bool AssistDot => Assist.IsRunning && Mode != AppMode.Assist;

    public string ToggleRecordingTitle => IsRecording ? "Stop Recording" : "Start Recording";
    public string RecordingMenuTitle => $"Recording {RecordingClock} · {RecordingMode.Title()}";
    public string TranscribingText => $"Transcribing {TranscribingCount} recording(s)…";

    /// Global error (spec 03 §1); shown as an alert by the main window.
    [ObservableProperty] string? _errorMessage;

    partial void OnErrorMessageChanged(string? value)
    {
        if (value is null) return;
        // Reset at once (the same text shows again next time) and show the modal alert from the dispatcher
        // queue: errors are reported from layout, binding and timer callbacks, where a nested
        // ShowDialog can fail ("dispatcher processing has been suspended").
        ErrorMessage = null;
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null) ShowError(value);
        else d.BeginInvoke(() => ShowError(value));
    }

    void ShowError(string value)
    {
        var settingsLink = value.Contains("System Settings") || value.Contains("Windows Settings") || value.Contains("Privacy & security");
        var buttons = settingsLink
            ? new[] { new AlertButton("OK", AlertButtonKind.Primary, IsDefault: true, IsCancel: true), new AlertButton("Open Windows Settings") }
            : new[] { new AlertButton("OK", AlertButtonKind.Primary, IsDefault: true, IsCancel: true) };
        var r = _windows.ShowAlert("MeetingAssistant", value, buttons);
        if (r == 1) _windows.OpenUrl("ms-settings:privacy-microphone");
    }

    // ---- Hooks for the feature phase (recording controller, import, Assist live) ----

    /// Start/stop recording requested (Ctrl+Shift+R, tray, recorder). Argument: the mode for tray "Record …", else null.
    public event Action<MeetingMode?>? RecordingToggleRequested;
    /// Import Audio… requested (Ctrl+I).
    public event Action? ImportRequested;
    /// "Suggest now" requested (Ctrl+Shift+Space, live view only).
    public event Action? SuggestNowRequested;
    /// Tray "Copy Last Dictation".
    public event Action? CopyLastDictationRequested;

    [RelayCommand] void ShowScribe() => Mode = AppMode.Scribe;
    [RelayCommand] void ShowAssist() => Mode = AppMode.Assist;
    [RelayCommand] void ToggleRecording() => RecordingToggleRequested?.Invoke(null);
    [RelayCommand] void RecordMode(MeetingMode mode) { RecordingMode = mode; RecordingToggleRequested?.Invoke(mode); }
    [RelayCommand] void ImportAudio() => ImportRequested?.Invoke();
    [RelayCommand] void ToggleFloating() => _windows.ToggleFloating();
    [RelayCommand] void OpenSettings() => _windows.ShowSettings();
    [RelayCommand] void ShowMain() => _windows.ShowMain();
    [RelayCommand] void Quit() => _windows.Quit();
    [RelayCommand] void CopyLastDictation() => CopyLastDictationRequested?.Invoke();

    [RelayCommand]
    void SuggestNow()
    {
        if (Mode == AppMode.Assist && Assist.IsRunning) SuggestNowRequested?.Invoke();
    }

    [RelayCommand]
    void OpenAya()
    {
        if (AyaUrl is not null) _windows.OpenUrl(AyaUrl);
    }
}
