using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Services;
using MeetingAssistant.Core.Capture;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;
using MeetingAssistant.Core.Scribe;
using MeetingAssistant.Core.Storage;

namespace MeetingAssistant.App.ViewModels;

/// A choice in a picker (value + title).
public sealed record Choice<T>(T Value, string Title)
{
    public override string ToString() => Title;
}

/// A row of the live transcript: clock · label (speaker colour) · text (muted while partial).
public sealed record LiveLineRow(string Id, string Time, string Label, Brush LabelBrush, string Text, bool IsFinal);

/// Scribe recorder screen (spec 01 "Recorder screen", "Live transcript view", "Model download").
public sealed partial class RecorderViewModel : ScreenViewModel
{
    readonly ScribeController _scribe;
    readonly AppServices _services = AppServices.Shared;

    public override string ScreenName => "Recorder";

    public RecorderViewModel(ScribeController scribe)
    {
        _scribe = scribe;
        _scribe.PropertyChanged += OnScribeChanged;
        _services.PreferencesChanged += () => AppServices.Ui(OnPreferencesChanged);
        _services.SpeakerLibraryChanged += () => AppServices.Ui(() => OnPropertyChanged(nameof(Hint)));
        RefreshDevices();
    }

    /// Recording state, levels and live lines.
    public ScribeController Controller => _scribe;

    void OnScribeChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScribeController.RecordingMode):
                OnPropertyChanged(nameof(Mode)); OnPropertyChanged(nameof(Explanation)); OnPropertyChanged(nameof(Hint));
                OnPropertyChanged(nameof(HintIcon)); OnPropertyChanged(nameof(ShowCallMeter)); OnPropertyChanged(nameof(RecordingEyebrow));
                break;
            case nameof(ScribeController.IsRecording):
                OnPropertyChanged(nameof(IsRecording)); OnPropertyChanged(nameof(RecordingEyebrow));
                if (!_scribe.IsRecording) LiveRows.Clear();
                break;
            case nameof(ScribeController.Elapsed): OnPropertyChanged(nameof(ElapsedText)); break;
            case nameof(ScribeController.MicLevel):
            case nameof(ScribeController.SystemLevel):
                OnPropertyChanged(nameof(MicMeter)); OnPropertyChanged(nameof(CallMeter)); OnPropertyChanged(nameof(WaveLevel));
                OnPropertyChanged(nameof(MicHot)); OnPropertyChanged(nameof(CallHot));
                break;
            case nameof(ScribeController.LiveLines): UpdateLiveRows(); break;
            case nameof(ScribeController.ModelsReady): OnPropertyChanged(nameof(ShowModelBanner)); break;
            case nameof(ScribeController.ModelSetup):
                OnPropertyChanged(nameof(IsDownloading)); OnPropertyChanged(nameof(DownloadFraction)); OnPropertyChanged(nameof(DownloadMessage));
                break;
            case nameof(ScribeController.InputDevices): OnPropertyChanged(nameof(Devices)); OnPropertyChanged(nameof(SelectedDevice)); break;
            case nameof(ScribeController.RecordingTitle): OnPropertyChanged(nameof(Title)); break;
            case nameof(ScribeController.ExpectedSpeakers): OnPropertyChanged(nameof(Participants)); break;
        }
    }

    void OnPreferencesChanged()
    {
        OnPropertyChanged(nameof(Language)); OnPropertyChanged(nameof(LiveTranscript)); OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(AutoTranscribe)); OnPropertyChanged(nameof(SelectedDevice)); OnPropertyChanged(nameof(ShowLive));
    }

    // ---- Not recording ----

    public IReadOnlyList<Choice<MeetingMode>> Modes { get; } =
        Enum.GetValues<MeetingMode>().Select(m => new Choice<MeetingMode>(m, m.Title())).ToArray();

    /// Selected mode (segmented control).
    public MeetingMode Mode
    {
        get => _scribe.RecordingMode;
        set
        {
            if (_scribe.RecordingMode == value) return;
            _scribe.RecordingMode = value;
            _scribe.Listen.ModeChanged();
        }
    }

    public string Explanation => Mode.Explanation();

    public string Title
    {
        get => _scribe.RecordingTitle;
        set => _scribe.RecordingTitle = value;
    }

    /// "System default" first, then the input devices.
    public IReadOnlyList<Choice<string>> Devices =>
        new[] { new Choice<string>("", "System default") }
            .Concat(_scribe.InputDevices.Select(d => new Choice<string>(d.Id, d.Name))).ToArray();

    /// The chosen microphone (`inputDeviceUID`; "" = system default).
    public string SelectedDevice
    {
        get => _services.Preferences.InputDeviceUID ?? "";
        set
        {
            var v = string.IsNullOrEmpty(value) ? null : value;
            if (v == _services.Preferences.InputDeviceUID) return;
            _services.UpdatePreferences(p => p.InputDeviceUID = v);
        }
    }

    public IReadOnlyList<Choice<int>> ParticipantOptions { get; } =
        ScribeRules.ParticipantOptions.Select(o => new Choice<int>(o.Value, o.Title)).ToArray();

    public int Participants
    {
        get => _scribe.ExpectedSpeakers;
        set => _scribe.ExpectedSpeakers = value;
    }

    public IReadOnlyList<Choice<string>> Languages { get; } =
        Enum.GetValues<TranscriptLanguage>().Select(l => new Choice<string>(l.Raw(), l.Title())).ToArray();

    /// Language preference (also the recorder's picker).
    public string Language
    {
        get => _services.Preferences.Language;
        set { if (value != _services.Preferences.Language) _services.UpdatePreferences(p => p.Language = value); }
    }

    /// "Show live transcript while recording".
    public bool LiveTranscript
    {
        get => _services.Preferences.LiveTranscription;
        set { if (value != _services.Preferences.LiveTranscription) _services.UpdatePreferences(p => p.LiveTranscription = value); }
    }

    public string Hint => ScribeRules.ModeHint(Mode, VoiceprintStore.Load() is not null, _services.Preferences.MyName);

    public string HintIcon => ScribeIcons.Glyph(Mode switch
    {
        MeetingMode.Call => "Icon.Headphones",
        MeetingMode.InPerson => "Icon.Voices",
        _ => "Icon.Info",
    });

    /// Re-reads the input devices (when the screen appears).
    [RelayCommand] public void RefreshDevices() => _scribe.RefreshDevices();

    [RelayCommand] Task StartStop() => _scribe.ToggleRecordingAsync();

    /// Opens the floating live transcript (does nothing when it is already open).
    [RelayCommand] void ToggleFloating() { if (!App.Current.IsFloatingOpen) App.Current.ToggleFloating(); }

    // ---- Models ----

    public bool ShowModelBanner => !_scribe.ModelsReady;
    public bool IsDownloading => _scribe.ModelSetup is not null;
    public double DownloadFraction => _scribe.ModelSetup?.Fraction ?? 0;
    public string DownloadMessage => _scribe.ModelSetup?.Message ?? "";

    [RelayCommand] void DownloadModels() => _scribe.PrepareModels();

    // ---- Recording ----

    public bool IsRecording => _scribe.IsRecording;
    public string RecordingEyebrow => $"Recording · {Mode.Title()}";
    public string ElapsedText => Exporter.Clock(_scribe.Elapsed);
    public double MicMeter => LevelMath.Meter(_scribe.MicLevel);
    public double CallMeter => LevelMath.Meter(_scribe.SystemLevel);
    /// Waveform level: max(mic, call) as meter fill.
    public double WaveLevel => LevelMath.Meter(Math.Max(_scribe.MicLevel, _scribe.SystemLevel));
    public bool ShowCallMeter => Mode.RecordsSystemAudio();
    /// Meters turn red above 90 %.
    public bool MicHot => MicMeter > 0.9;
    public bool CallHot => CallMeter > 0.9;
    public bool AutoTranscribe => _services.Preferences.AutoTranscribe;
    /// The live transcript is shown while recording.
    public bool ShowLive => _services.Preferences.LiveTranscription;

    /// Live transcript rows (newest last).
    public ObservableCollection<LiveLineRow> LiveRows { get; } = new();

    void UpdateLiveRows()
    {
        var labels = new LiveSpeakerLabels(Mode, _services.Preferences.MyName);
        var subtle = ScribeIcons.Theme("TextSubtleBrush");
        var rows = _scribe.LiveLines.Select(l => new LiveLineRow(
            l.Id, Exporter.Clock(l.Start), labels.Label(l.Speaker),
            l.Speaker is null ? subtle : ScribeIcons.Speaker(l.Speaker, l.Speaker == "me"),
            l.Text, l.IsFinal)).ToList();
        // Replace in place: unchanged rows keep their containers (no flicker).
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < LiveRows.Count) { if (LiveRows[i] != rows[i]) LiveRows[i] = rows[i]; }
            else LiveRows.Add(rows[i]);
        }
        while (LiveRows.Count > rows.Count) LiveRows.RemoveAt(LiveRows.Count - 1);
    }

    /// Opens the Stop Earlier sheet.
    [RelayCommand]
    void StopEarlier()
    {
        if (!_scribe.IsRecording) return;
        var vm = new StopEarlierViewModel(_scribe);
        if (Views.SheetHost.Show(vm) && vm.Confirmed) _ = _scribe.StopRecordingAsync(vm.KeepUntil);
    }
}
