using System.ComponentModel;
using System.Windows;
using MeetingAssistant.App.Services;
using MeetingAssistant.Core.Model;
using MeetingAssistant.Core.Storage;

namespace MeetingAssistant.App;

/// Composition root and the Scribe area: shared services, recording, transcription queue,
/// launch recovery and the floating transcript (spec 01).
public partial class App
{
    /// Shared services (preferences, vocabulary, queue…); see <see cref="AppServices"/>.
    public AppServices Services { get; private set; } = null!;

    /// Scribe's recording / session controller.
    public ScribeController Scribe { get; private set; } = null!;

    partial void ConfigureServices()
    {
        Services = AppServices.Create(SettingsStore.Shared);
        UiState = new SettingsUiState(Services.Settings, Services);
        Scribe = new ScribeController(Services);
    }

    partial void StartScribe()
    {
        var scribe = Scribe;
        var shell = Shell;

        // Errors go to the global alert.
        scribe.Error += message => AppServices.Ui(() => shell.ErrorMessage = message);

        // Recording state for the rail, tray and floating window.
        shell.RecordingMode = scribe.RecordingMode;
        scribe.PropertyChanged += (_, e) => AppServices.Ui(() => SyncShell(e));
        shell.RecordingToggleRequested += mode =>
        {
            if (mode is { } m && !scribe.IsRecording) scribe.RecordingMode = m;
            _ = scribe.ToggleRecordingAsync();
        };
        shell.ImportRequested += ImportAudio;

        // Floating window: listening follows the window.
        Floating.Attach(scribe.Listen, scribe);
        FloatingOpened += Floating.Opened;
        FloatingClosed += Floating.Closed;
        if (IsFloatingOpen) Floating.Opened();

        // Never lose a recording: finish it before the app goes away.
        Quitting += scribe.FinishForQuit;
        SessionEnding += (_, _) => scribe.FinishForQuit();

        // Sessions interrupted by a crash or quit: fix `recording`, re-queue `queued` / `transcribing`.
        try { LaunchRecovery.Run(Services.Queue.Enqueue); }
        catch (Exception e) { shell.ErrorMessage = $"Could not recover interrupted recordings: {e.Message}"; }
        scribe.Reload();
    }

    void SyncShell(PropertyChangedEventArgs e)
    {
        var scribe = Scribe;
        var shell = Shell;
        switch (e.PropertyName)
        {
            case nameof(ScribeController.IsRecording):
                shell.IsRecording = scribe.IsRecording;
                if (!scribe.IsRecording) shell.RecordingClock = "0:00";
                break;
            case nameof(ScribeController.Elapsed):
                shell.RecordingClock = Core.Storage.Exporter.Clock(scribe.Elapsed);
                break;
            case nameof(ScribeController.RecordingMode):
                shell.RecordingMode = scribe.RecordingMode;
                break;
            case nameof(ScribeController.TranscribingCount):
                shell.TranscribingCount = scribe.TranscribingCount;
                break;
        }
    }

    /// Import Audio… (Ctrl+I): one file (In person) or two (Call: microphone first, then the call audio).
    void ImportAudio()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose one recording (in-person meeting), or two files: first your microphone, then the call audio.",
            Multiselect = true,
            Filter = "Audio files|*.wav;*.mp3;*.m4a;*.aac;*.caf;*.flac;*.wma;*.mp4;*.ogg|All files|*.*",
        };
        if (dialog.ShowDialog(MainWindow) != true || dialog.FileNames.Length == 0) return;
        var files = dialog.FileNames;
        if (files.Length > 2)
        {
            Shell.ErrorMessage = "Import failed: choose one or two audio files.";
            return;
        }
        Shell.Mode = ViewModels.AppMode.Scribe;
        _ = Scribe.ImportFilesAsync(files);
    }

    partial void StopScribe()
    {
        if (Scribe is null) return;
        Scribe.FinishForQuit();
        Scribe.Listen.Shutdown();
    }
}
