using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAssistant.App.Services;
using MeetingAssistant.Core;
using MeetingAssistant.Core.Capture;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Pipeline;
using MeetingAssistant.Core.Scribe;
using MeetingAssistant.Core.Storage;
using MeetingAssistant.Platform.Windows.Audio;
using MeetingAssistant.Platform.Windows.SystemServices;
using MeetingAssistant.Speech;

namespace MeetingAssistant.App.ViewModels;

/// Settings › General (spec 04 §6.1): name, default meeting type, live / auto transcription, engine,
/// language, microphone and the recordings folder. Saved immediately on change.
public sealed partial class GeneralSettingsViewModel : ObservableObject
{
    readonly AppServices _services = AppServices.Shared;

    public GeneralSettingsViewModel()
    {
        _services.PreferencesChanged += () => AppServices.Ui(() => OnPropertyChanged(string.Empty));
        ScribeController.Shared.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScribeController.InputDevices)) OnPropertyChanged(nameof(Devices));
        };
    }

    Preferences P => _services.Preferences;

    public string MyName
    {
        get => P.MyName;
        set { if (value != P.MyName) _services.UpdatePreferences(p => p.MyName = value); }
    }

    public IReadOnlyList<Choice<MeetingMode>> Modes { get; } =
        Enum.GetValues<MeetingMode>().Select(m => new Choice<MeetingMode>(m, m.Title())).ToArray();

    public MeetingMode DefaultMode
    {
        get => P.DefaultMode;
        set { if (value != P.DefaultMode) _services.UpdatePreferences(p => p.DefaultMode = value); }
    }

    public bool LiveTranscription
    {
        get => P.LiveTranscription;
        set { if (value != P.LiveTranscription) _services.UpdatePreferences(p => p.LiveTranscription = value); }
    }

    public bool AutoTranscribe
    {
        get => P.AutoTranscribe;
        set { if (value != P.AutoTranscribe) _services.UpdatePreferences(p => p.AutoTranscribe = value); }
    }

    public bool HideFromScreenSharing
    {
        get => P.HideFromScreenSharing;
        set { if (value != P.HideFromScreenSharing) _services.UpdatePreferences(p => p.HideFromScreenSharing = value); }
    }

    /// Windows has Parakeet only (Qwen3-ASR has no word aligner in sherpa-onnx); a stored Qwen3
    /// preference is shown as Parakeet, which is what runs.
    public IReadOnlyList<Choice<string>> Engines { get; } =
        new[] { new Choice<string>(AsrEngine.ParakeetUltra.Raw(), AsrEngine.ParakeetUltra.Title()) };

    public string Engine
    {
        get => AsrEngine.ParakeetUltra.Raw();
        set { if (value != P.Engine) _services.UpdatePreferences(p => p.Engine = value); }
    }

    public IReadOnlyList<Choice<string>> Languages { get; } =
        Enum.GetValues<TranscriptLanguage>().Select(l => new Choice<string>(l.Raw(), l.Title())).ToArray();

    public string Language
    {
        get => P.Language;
        set { if (value != P.Language) _services.UpdatePreferences(p => p.Language = value); }
    }

    public IReadOnlyList<Choice<string>> Devices =>
        new[] { new Choice<string>("", "System default") }
            .Concat(ScribeController.Shared.InputDevices.Select(d => new Choice<string>(d.Id, d.Name))).ToArray();

    /// Microphone (`inputDeviceUID`; "" = system default). Also used by dictation.
    public string InputDevice
    {
        get => P.InputDeviceUID ?? "";
        set
        {
            var v = string.IsNullOrEmpty(value) ? null : value;
            if (v != P.InputDeviceUID) _services.UpdatePreferences(p => p.InputDeviceUID = v);
        }
    }

    [RelayCommand] void RefreshDevices() => ScribeController.Shared.RefreshDevices();

    /// "Recordings folder › Show in Explorer".
    [RelayCommand]
    void ShowRecordings()
    {
        try { Directory.CreateDirectory(AppPaths.Sessions); MeetingAssistant.Platform.Windows.SystemServices.Shell.Open(AppPaths.Sessions); }
        catch (Exception e) { ScribeController.Shared.ReportError(e.Message); }
    }
}

/// A remembered voice row: "name · from N meeting(s)" with Forget.
public sealed record VoiceEntry(string Name, string CountText);

/// Settings › Voices (spec 04 §6.4, 01 "Voiceprint", "Speaker library").
public sealed partial class VoicesSettingsViewModel : ObservableObject
{
    readonly AppServices _services = AppServices.Shared;
    readonly object _bufferLock = new();
    List<float> _buffer = new();
    WasapiMicSource? _mic;
    DispatcherTimer? _timer;

    public VoicesSettingsViewModel()
    {
        _services.SpeakerLibraryChanged += () => AppServices.Ui(Reload);
        Reload();
    }

    /// The German + English text to read aloud.
    public string ReadingText =>
        "Bitte lies diesen Text in normaler Lautstärke vor: Heute bespreche ich mit dem Team die nächsten Schritte im Projekt. " +
        "Wir schauen uns die Zahlen an, klären offene Fragen und verteilen die Aufgaben für die kommende Woche. " +
        "Then continue in English: we review the timeline, agree on priorities and write down the action items.";

    public string Explanation =>
        $"MeetingAssistant recognises you by your voice. Record a voice sample once: read the text aloud for at least " +
        $"{(int)VoiceprintStore.RecordingSeconds} seconds ({(int)VoiceprintStore.MinimumSpeechSeconds} seconds of speech are needed).";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVoiceprint), nameof(VoiceprintText), nameof(RecordTitle))]
    Voiceprint? _voiceprint;

    public bool HasVoiceprint => Voiceprint is not null;
    public string VoiceprintText => Voiceprint is { } v ? ScribeRules.VoiceprintText(v) : "";
    public string RecordTitle => Voiceprint is null ? "Record Voice Sample" : "Record Again";

    public ObservableCollection<VoiceEntry> Library { get; } = new();
    [ObservableProperty] bool _libraryEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRecording))]
    bool _isRecording;
    public bool NotRecording => !IsRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondsText), nameof(FinishTitle), nameof(CanFinish))]
    double _seconds;

    public string SecondsText => $"{Seconds:0} s";
    public string FinishTitle => ScribeRules.EnrollmentButton(Seconds);
    public bool CanFinish => Seconds >= VoiceprintStore.RecordingSeconds;

    /// Meter fill of the enrollment level.
    [ObservableProperty] double _level;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRecord))]
    bool _busy;
    public bool CanRecord => !Busy;

    [ObservableProperty] string? _message;

    void Reload()
    {
        Voiceprint = VoiceprintStore.Load();
        Library.Clear();
        foreach (var e in SpeakerLibraryStore.Load().Entries)
            Library.Add(new VoiceEntry(e.Name, ScribeRules.LibraryCount(e.Count)));
        LibraryEmpty = Library.Count == 0;
    }

    [RelayCommand]
    void DeleteVoiceprint()
    {
        VoiceprintStore.Delete();
        Voiceprint = null;
        _services.NotifySpeakersChanged();
    }

    [RelayCommand]
    void Forget(VoiceEntry? entry)
    {
        if (entry is null) return;
        _services.UpdateSpeakerLibrary(lib => lib.Forget(entry.Name));
    }

    [RelayCommand]
    void Record()
    {
        Message = null;
        if (MicrophonePrivacy.Check() == MicrophoneAccess.Denied) { Message = "Microphone access is needed."; return; }
        if (ScribeController.Shared.IsRecording) { Message = "Stop the recording first."; return; }
        lock (_bufferLock) _buffer = new List<float>();
        try
        {
            var mic = new WasapiMicSource(_services.Preferences.InputDeviceUID);
            mic.Samples += s => { lock (_bufferLock) _buffer.AddRange(s); };
            mic.Start();
            _mic = mic;
        }
        catch (Exception e)
        {
            Message = e is MicrophoneAccessDeniedException ? "Microphone access is needed." : e.Message;
            return;
        }
        Seconds = 0;
        IsRecording = true;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(0.1), DispatcherPriority.Normal, (_, _) => Tick(), Application.Current.Dispatcher);
        _timer.Start();
    }

    void Tick()
    {
        float[] tail;
        int n;
        lock (_bufferLock)
        {
            n = _buffer.Count;
            tail = _buffer.Skip(Math.Max(0, n - 1600)).ToArray();
        }
        Seconds = n / 16_000.0;
        Level = LevelMath.Meter(LevelMath.Rms(tail));
    }

    float[] StopMic()
    {
        _timer?.Stop(); _timer = null;
        _mic?.Stop(); _mic?.Dispose(); _mic = null;
        IsRecording = false;
        Level = 0;
        lock (_bufferLock) return _buffer.ToArray();
    }

    /// Finish: enrolls the voiceprint under the current name.
    [RelayCommand]
    async Task Finish()
    {
        if (!CanFinish) return;
        var samples = StopMic();
        Busy = true;
        var name = _services.Preferences.MyName;
        try
        {
            var v = await Task.Run(() => Enrollment.Enroll(name, samples));
            Voiceprint = v;
            Message = $"Saved. You will be labelled “{name}” in in-person meetings.";
            _services.NotifySpeakersChanged();
        }
        catch (Exception e) { Message = e.Message; }
        Busy = false;
    }

    /// Leaving the page stops a running sample recording.
    public void Cancel() { if (IsRecording) StopMic(); }
}

/// One model row in Settings › Models.
public sealed record ModelRow(string Title, string Detail, string Size);

/// Settings › Models (spec 04 §6.5, 01 "Model download"; Windows: sherpa-onnx models in `<root>\Models`).
public sealed partial class ModelsSettingsViewModel : ObservableObject
{
    readonly ScribeController _scribe = ScribeController.Shared;

    public ModelsSettingsViewModel()
    {
        _scribe.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ScribeController.ModelSetup) or nameof(ScribeController.ModelsReady))
            {
                OnPropertyChanged(nameof(IsDownloading)); OnPropertyChanged(nameof(Fraction)); OnPropertyChanged(nameof(Message));
                OnPropertyChanged(nameof(DownloadTitle)); OnPropertyChanged(nameof(CanDelete));
                if (e.PropertyName == nameof(ScribeController.ModelsReady) || _scribe.ModelSetup is null) Refresh();
            }
            if (e.PropertyName is nameof(ScribeController.IsRecording) or nameof(ScribeController.TranscribingCount))
                OnPropertyChanged(nameof(CanDelete));
        };
        Refresh();
    }

    public ObservableCollection<ModelRow> Models { get; } = new();

    [ObservableProperty] string _totalSize = "";

    public bool IsDownloading => _scribe.ModelSetup is not null;
    public double Fraction => _scribe.ModelSetup?.Fraction ?? 0;
    public string Message => _scribe.ModelSetup?.Message ?? "";
    public string DownloadTitle => _scribe.ModelsReady ? "Check / Re-download Models" : "Download Models";
    /// Deleting is refused while models are in use or downloading.
    public bool CanDelete => !IsDownloading && !_scribe.IsRecording && _scribe.TranscribingCount == 0 && Models.Any(m => m.Size != "Not downloaded");

    /// Re-reads the model folders and their sizes.
    public void Refresh()
    {
        Models.Clear();
        long total = 0;
        foreach (var (spec, detail) in new[]
        {
            (ModelCatalog.Parakeet, "Transcription · NVIDIA Parakeet TDT 0.6B (int8, sherpa-onnx)"),
            (ModelCatalog.Segmentation, "Speakers · pyannote segmentation 3.0"),
            (ModelCatalog.CampPlus, "Voice matching · CAM++ speaker embeddings"),
            (ModelCatalog.SileroVad, "Speech detection · Silero VAD"),
        })
        {
            long size = FolderSize(spec.Directory);
            total += size;
            Models.Add(new ModelRow(spec.Title, detail, spec.IsReady ? FormatSize(size) : "Not downloaded"));
        }
        TotalSize = total > 0 ? $"{FormatSize(total)} in {AppPaths.Models}" : "";
        OnPropertyChanged(nameof(CanDelete));
    }

    static long FolderSize(string dir)
    {
        try { return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; }
        catch { return 0; }
    }

    /// "530 MB", "7.1 MB", "640 KB".
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 10L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    [RelayCommand] void Download() => _scribe.PrepareModels();

    /// Deletes the downloaded models (asks first). They are downloaded again when needed.
    [RelayCommand]
    void Delete()
    {
        if (!CanDelete) return;
        var r = App.Current.ShowConfirm("Delete the speech models?",
            "Recording still works, but transcription, live transcripts and dictation need the models until you download them again.",
            new AlertButton("Delete", AlertButtonKind.Destructive, IsDefault: true), new AlertButton("Cancel", IsCancel: true));
        if (r != 0) return;
        _scribe.Listen.StopListening();
        try
        {
            SpeechEngine.Shared.UnloadTranscriptionModels();
            foreach (var spec in ModelCatalog.Steps.Select(s => s.Model))
                if (Directory.Exists(spec.Directory)) Directory.Delete(spec.Directory, true);
        }
        catch (Exception e) { _scribe.ReportError($"Could not delete the models: {e.Message}"); }
        _scribe.ModelsReady = ModelCatalog.CoreModelsReady();
        Refresh();
    }
}
